/** Hide unmarked Acceptance cases in the view even when an older server returns full content.
 * Server-side projection is still required: this cannot prevent sensitive network delivery.
 */
import { isRecord } from '../composables/format'
import type { ApiResource } from './types'
export function projectDataset<TBody>(resource: ApiResource<TBody>): ApiResource<TBody> {
  const body = resource.body
  if (!isRecord(body) || body.purpose !== 'Acceptance' || !Array.isArray(body.cases)) { return resource }
  const cases = body.cases.map(item => {
    if (!isRecord(item) || item.holdoutWithheld === false) { return item }
    return { ...item, input: null, reference: null, assets: [], holdoutWithheld: true }
  })
  return { ...resource, body: { ...body, cases } as TBody }
}
