using System.Text.Json;
using NKeeper.Abstractions;
using NKeeper.Plugins.Databases;

namespace NKeeper.Plugins.Ase.Tests;

/// <summary>
/// The ASE engine against a real SAP ASE 16.0 (docs/architecture.md §7.35): signing in, then db-core's session on it,
/// as the engine's session type opens it.
/// </summary>
public sealed class AseTests(AseFixture ase)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Signs_in_saying_the_driver_keeps_the_password()
    {
        ase.SkipIfUnavailable();
        var core = Core();

        await using var login = await new AseEngine(core, new FakeBroker(AseFixture.UserName)).OpenAsync(Requests.Session(), Ct);

        // The manifest declares it (credentialRelease), so core hands the connection over (SDK 1.10).
        Assert.True(Assert.Single(core.Requests).DriverKeepsPassword);
        var info = await new AseDialect().ServerInfoAsync(login.Connection, Ct);
        Assert.Equal("sa", info.Login);
        Assert.Contains("16.0", info.ProductVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_password_and_a_domain_are_refused_in_words()
    {
        ase.SkipIfUnavailable();

        var wrong = await Assert.ThrowsAsync<ConnectException>(() => new AseEngine(Core("not-the-password"), new FakeBroker(AseFixture.UserName)).OpenAsync(Requests.Session(), Ct));
        Assert.DoesNotContain("not-the-password", wrong.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectFailure.AuthenticationFailed, wrong.Reason);

        var domain = await Assert.ThrowsAsync<ConnectException>(() => new AseEngine(Core(), new FakeBroker(AseFixture.UserName, "CORP")).OpenAsync(Requests.Session(), Ct));
        Assert.Contains("no domain", domain.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batches_run_with_results_PRINT_and_errors_at_their_lines()
    {
        ase.SkipIfUnavailable();
        await using var session = await SessionAsync();
        var window = await session.WindowAsync();

        var result = await session.Channel.ReplyAsync("query", new
        {
            window,
            sql = """
                PRINT 'starting'
                SELECT 1 AS one, 'two' AS two, convert(int, NULL) AS nothing
                go
                SELECT * FROM does_not_exist
                go
                SELECT 'after the error' AS still
                """,
        });

        var output = result.GetProperty("output").EnumerateArray().ToList();
        Assert.Contains(output, o => o.GetProperty("kind").GetString() == "message" && o.GetProperty("text").GetString() == "starting");
        var first = output.First(o => o.GetProperty("kind").GetString() == "results");
        Assert.Equal(["one", "two", "nothing"], first.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal(["1", "two", null], first.GetProperty("rows")[0].EnumerateArray().Select(v => v.GetString()));
        var error = output.First(o => o.GetProperty("kind").GetString() == "error");
        Assert.Equal(4, error.GetProperty("line").GetInt32());
        Assert.Equal("after the error", output.Last(o => o.GetProperty("kind").GetString() == "results").GetProperty("rows")[0][0].GetString());
        Assert.True(result.GetProperty("hasErrors").GetBoolean());
    }

    [Fact]
    public async Task A_window_changes_database_and_the_explorer_and_scripts_work()
    {
        ase.SkipIfUnavailable();
        var table = $"t{Guid.NewGuid():N}"[..20];
        await ase.ExecuteAsync($"CREATE TABLE {table} (id int NOT NULL PRIMARY KEY, name varchar(50) NULL)", "tempdb", Ct);
        await using var session = await SessionAsync();
        var window = await session.WindowAsync();

        Assert.Equal("tempdb", (await session.Channel.ReplyAsync("use", new { window, name = "tempdb" })).GetProperty("name").GetString());
        Assert.Contains("tempdb", (await session.Channel.ReplyAsync("databases")).EnumerateArray().Select(d => d.GetString()));

        var server = await session.ExploreAsync(new { kind = "server" });
        Assert.Equal(["Databases", "Security", "Management"], server.Select(Label));
        var tempdb = (await session.ExploreAsync((await session.ExploreAsync(server[0]))[0])).Single(n => Label(n) == "tempdb");
        var tables = (await session.ExploreAsync(tempdb))[0];
        var node = (await session.ExploreAsync(tables)).Single(n => n.GetProperty("name").GetString() == table);
        Assert.Equal(["selectTop", "create"], node.GetProperty("scripts").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(["id", "name"], (await session.ExploreAsync((await session.ExploreAsync(node))[0])).Select(Label));
        Assert.Equal($"USE [tempdb]\ngo\n\nSELECT TOP 1000 *\nFROM [dbo].[{table}]\ngo\n", await session.ScriptAsync(node, "selectTop"));
        Assert.Contains($"CREATE TABLE", await session.ScriptAsync(node, "create"), StringComparison.Ordinal);
        Assert.Equal("blocking", Assert.Single(await session.ExploreAsync(server[2])).GetProperty("tool").GetString());
    }

    [Fact]
    public async Task The_blocking_manager_shows_who_blocks_whom_and_kills_the_lead()
    {
        ase.SkipIfUnavailable();
        var table = $"b{Guid.NewGuid():N}"[..20];
        await ase.ExecuteAsync($"CREATE TABLE {table} (id int NOT NULL PRIMARY KEY, balance int) lock datarows", "tempdb", Ct);
        await ase.ExecuteAsync($"INSERT INTO {table} VALUES (1, 100)", "tempdb", Ct);
        await using var session = await SessionAsync();

        await using var lead = await ase.OpenAsync("tempdb", Ct);
        int leadId;
        await using (var begin = lead.CreateCommand())
        {
            begin.CommandText = $"BEGIN TRAN UPDATE {table} SET balance = 0 WHERE id = 1 SELECT @@spid";
            leadId = Convert.ToInt32(await begin.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
        }

        await using var waiter = await ase.OpenAsync("tempdb", Ct);
        await using var wait = waiter.CreateCommand();
        wait.CommandText = $"UPDATE {table} SET balance = 1 WHERE id = 1";
        var waiting = Task.Run(async () =>
        {
            try
            {
                await wait.ExecuteNonQueryAsync(Ct);
            }
            catch (AdoNetCore.AseClient.AseException)
            {
                // Its blocker was killed under it.
            }
        }, Ct);
        await Task.Delay(1500, Ct);

        var chain = Assert.Single((await session.Channel.ReplyAsync("blocking")).GetProperty("leads").EnumerateArray());
        Assert.Equal((leadId, 1), (chain.GetProperty("sessionId").GetInt32(), chain.GetProperty("blockedVolume").GetInt32()));

        await session.Channel.ReplyAsync("kill", new { sessionId = leadId });
        await waiting.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal("ase.kill", Assert.Single(session.Audit.Events).Action);
    }

    private static string? Label(JsonElement node) => node.GetProperty("label").GetString();

    private FakeConnectivity Core(string password = AseFixture.Password) => new(ase.Port, AseFixture.UserName, password);

    /// <summary>A database tab, as the engine's session type opens it: db-core's handler, with this engine.</summary>
    private async Task<Session> SessionAsync()
    {
        var audit = new FakeAudit();
        var handler = new DatabaseSessionHandler<AseEngine>(new AseEngine(Core(), new FakeBroker(AseFixture.UserName)), audit, TimeProvider.System);
        var stream = await handler.OpenAsync(Requests.Session(), Ct);
        var channel = new FakeChannel();
        return new Session(stream, channel, audit, stream.RunAsync(channel, Ct));
    }

    private sealed class Session(IStreamSession stream, FakeChannel channel, FakeAudit audit, Task running) : IAsyncDisposable
    {
        public FakeChannel Channel => channel;

        public FakeAudit Audit => audit;

        public async Task<string> WindowAsync() => (await channel.ReplyAsync("window.open")).GetProperty("window").GetString()!;

        public async Task<List<JsonElement>> ExploreAsync(object node) => [.. (await channel.ReplyAsync("explore", new { node })).EnumerateArray()];

        public async Task<string> ScriptAsync(JsonElement node, string action) => (await channel.ReplyAsync("script", new { node, action })).GetProperty("sql").GetString()!;

        public async ValueTask DisposeAsync()
        {
            channel.Close();
            await running;
            await stream.DisposeAsync();
        }
    }
}
