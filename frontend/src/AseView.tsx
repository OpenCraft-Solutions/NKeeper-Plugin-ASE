import type { SessionViewProps } from '@nkeeper/sdk'
import { dbCore, type DatabaseEngineInfo } from './dbCore'

const ASE: DatabaseEngineInfo = {
  product: 'SAP ASE',
  databaseLabel: 'Database',
  placeholder: 'SELECT name FROM master..sysdatabases',
}

/** The SAP ASE tab: db-core's database tab, told it's ASE. */
export function AseView(props: SessionViewProps) {
  const shared = dbCore()
  return shared ? <shared.DatabaseView {...props} engine={ASE} /> : null
}
