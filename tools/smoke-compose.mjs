// Run after the full Compose is healthy: node tools/smoke-compose.mjs
// Uses synthetic data, real cookie/CSRF authentication, and the public same-origin proxy.
import assert from 'node:assert/strict'
import { setTimeout as delay } from 'node:timers/promises'
import { execFileSync } from 'node:child_process'

const base = process.env.DATAFLOW_WEB_URL ?? 'http://127.0.0.1:8080'
const jaeger = process.env.DATAFLOW_JAEGER_URL ?? 'http://127.0.0.1:16686'
const prometheus = process.env.DATAFLOW_PROMETHEUS_URL ?? 'http://127.0.0.1:9090'
const cookies = new Map()
let csrf
async function request(path, options = {}) {
  const headers = new Headers(options.headers)
  headers.set('Cookie', [...cookies].map(([key, value]) => `${key}=${value}`).join('; '))
  if (csrf) headers.set('X-CSRF-TOKEN', csrf)
  const response = await fetch(`${base}${path}`, { ...options, headers })
  for (const cookie of response.headers.getSetCookie()) {
    const pair = cookie.split(';')[0]
    const index = pair.indexOf('=')
    cookies.set(pair.slice(0, index), pair.slice(index + 1))
  }
  assert.ok(response.ok, `${path}: ${response.status} ${response.ok ? '' : await response.text()}`)
  return response.status === 204 ? null : response.json()
}
async function post(path, body) {
  return request(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
}
async function waitFor(action, label, seconds = 90) {
  const deadline = Date.now() + seconds * 1000
  while (Date.now() < deadline) {
    const result = await action()
    if (result) return result
    await delay(1000)
  }
  throw new Error(`Timeout: ${label}`)
}

csrf = (await request('/api/v1/auth/antiforgery')).token
const credentials = { email: `compose-${crypto.randomUUID()}@example.test`, password: 'Synthetic-Stage11!42' }
await post('/api/v1/auth/register', credentials)
await post('/api/v1/auth/login', credentials)
csrf = (await request('/api/v1/auth/antiforgery')).token
assert.equal((await request('/api/v1/auth/me')).email, credentials.email)
const source = await post('/api/v1/data-sources', {
  name: 'Stage 11 Compose smoke', acceptedFormats: ['Csv'], schema: {
    keyFields: ['externalId'], fields: [
      { name: 'externalId', type: 'string', required: true, maxLength: 80 },
      { name: 'name', type: 'string', required: true, maxLength: 200 },
      { name: 'email', type: 'email', required: true, maxLength: 254 },
      { name: 'isActive', type: 'boolean', required: true },
    ],
  },
})
async function upload(csv) {
  const traceId = crypto.randomUUID().replaceAll('-', '')
  const form = new FormData()
  form.append('file', new Blob([csv], { type: 'text/csv' }), 'synthetic.csv')
  const accepted = await request(`/api/v1/data-sources/${source.id}/files`, {
    method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID(), traceparent: `00-${traceId}-0123456789abcdef-01` }, body: form,
  })
  const job = await waitFor(async () => {
    const current = await request(`/api/v1/jobs/${accepted.jobId}`)
    return current.completedAt ? current : null
  }, 'import completion')
  return { job, traceId }
}
const valid = await upload('externalId,name,email,isActive\n1,Synthetic,synthetic@example.test,true\n2,,bad,false\n')
assert.equal(valid.job.status, 'CompletedWithErrors')
assert.equal(valid.job.processedRows, 2)
assert.equal(valid.job.insertedRows, 1)
assert.equal(valid.job.invalidRows, 1)
const failed = await upload('wrong,header\n1,2\n')
assert.equal(failed.job.status, 'Failed')
const spans = await waitFor(async () => {
  const response = await fetch(`${jaeger}/api/traces/${failed.traceId}`)
  if (!response.ok) return null
  const data = await response.json()
  const all = data.data?.flatMap(trace => trace.spans) ?? []
  return ['import upload', 'import publish', 'import process'].every(name => all.some(span => span.operationName === name)) ? all : null
}, 'exported HTTP -> outbox -> RabbitMQ -> Worker trace')
assert.ok(spans.some(span => span.tags?.some(tag => tag.key === 'error' && tag.value === true)))
assert.ok(spans.some(span => span.tags?.some(tag => tag.key === 'job.id' && tag.value === failed.job.id)))
for (const span of spans) {
  assert.ok(!span.tags?.some(tag => ['db.statement', 'db.query.text'].includes(tag.key)), 'SQL text leaked into telemetry')
}
await waitFor(async () => {
  const response = await fetch(`${prometheus}/api/v1/query?query=${encodeURIComponent('dataflow_import_rows_total')}`)
  const data = await response.json()
  return data.data?.result?.some(result => Number(result.value[1]) > 0)
}, 'exported committed-row metrics')
if (process.argv.includes('--recreate-api')) {
  const compose = ['compose', '--env-file', process.env.DATAFLOW_COMPOSE_ENV_FILE ?? '.env']
  if (process.env.DATAFLOW_COMPOSE_PROJECT) compose.push('-p', process.env.DATAFLOW_COMPOSE_PROJECT)
  compose.push('-f', 'deploy/docker-compose.yml', '-f', 'deploy/docker-compose.app.yml')
  execFileSync('docker', [...compose, 'up', '-d', '--no-deps', '--force-recreate', '--wait', 'api'], { stdio: 'inherit' })
  // The same in-memory cookie must still authenticate and existing history must survive.
  assert.equal((await request('/api/v1/auth/me')).email, credentials.email)
  assert.equal((await request(`/api/v1/jobs/${valid.job.id}`)).processedRows, 2)
}
console.log(JSON.stringify({ result: 'passed', jobId: valid.job.id, failedJobId: failed.job.id, traceId: failed.traceId, spans: spans.length }, null, 2))
