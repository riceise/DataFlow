// Stage 12 acceptance demo: shows that stopping the Worker mid-processing
// neither loses the job nor creates duplicates, and that a file accepted while
// the Worker is down survives in PostgreSQL/RabbitMQ and is processed after
// the Worker returns.
//
// Requires the full Compose stack (see deploy/OPERATIONS.md):
//   docker compose --env-file .env -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml up -d --wait
//   node tools/demo-recovery.mjs
//
// Optional environment: DATAFLOW_WEB_URL, DATAFLOW_COMPOSE_ENV_FILE,
// DATAFLOW_COMPOSE_PROJECT, DATAFLOW_STOP_TIMEOUT.
import assert from 'node:assert/strict'
import { setTimeout as delay } from 'node:timers/promises'
import { execFileSync } from 'node:child_process'

const base = process.env.DATAFLOW_WEB_URL ?? 'http://127.0.0.1:8080'
const composeEnv = process.env.DATAFLOW_COMPOSE_ENV_FILE ?? '.env'
const stopTimeout = process.env.DATAFLOW_STOP_TIMEOUT ?? '60'
const compose = ['compose', '--env-file', composeEnv]
if (process.env.DATAFLOW_COMPOSE_PROJECT) compose.push('-p', process.env.DATAFLOW_COMPOSE_PROJECT)
compose.push('-f', 'deploy/docker-compose.yml', '-f', 'deploy/docker-compose.app.yml')
const cookies = new Map()
let csrf

function docker(action, ...rest) {
  execFileSync('docker', [...compose, action, ...rest], { stdio: 'inherit' })
}

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

async function waitFor(action, label, seconds = 240, interval = 1000) {
  const deadline = Date.now() + seconds * 1000
  while (Date.now() < deadline) {
    const result = await action()
    if (result) return result
    await delay(interval)
  }
  throw new Error(`Timeout: ${label}`)
}

async function upload(name, csv) {
  const form = new FormData()
  form.append('file', new Blob([csv], { type: 'text/csv' }), name)
  const accepted = await request(`/api/v1/data-sources/${source.id}/files`, {
    method: 'POST',
    headers: { 'Idempotency-Key': crypto.randomUUID() },
    body: form,
  })
  return accepted.jobId
}

async function job(id) {
  return request(`/api/v1/jobs/${id}`)
}

const bigRows = 40_000
const bigCsv = 'externalId,name,email,registeredAt,isActive\n' +
  Array.from({ length: bigRows }, (_, index) =>
    `rec-${index},Recovery Demo ${index},recovery-${index}@example.test,2026-01-15,true`).join('\n') + '\n'
const smallValid = 50
const smallInvalid = 150
const smallCsv = 'externalId,name,email,registeredAt,isActive\n' +
  Array.from({ length: smallValid }, (_, index) =>
    `small-${index},Small File ${index},small-${index}@example.test,2026-01-15,true`).join('\n') + '\n' +
  Array.from({ length: smallInvalid }, (_, index) =>
    `small-invalid-${index},,small-invalid-${index}@example.test,2026-01-15,true`).join('\n') + '\n'

csrf = (await request('/api/v1/auth/antiforgery')).token
const credentials = { email: `recovery-${crypto.randomUUID()}@example.test`, password: 'Recovery-2026!42' }
await post('/api/v1/auth/register', credentials)
await post('/api/v1/auth/login', credentials)
csrf = (await request('/api/v1/auth/antiforgery')).token
const source = await post('/api/v1/data-sources', {
  name: 'Recovery demo', acceptedFormats: ['Csv'], schema: {
    keyFields: ['externalId'], fields: [
      { name: 'externalId', type: 'string', required: true, maxLength: 80 },
      { name: 'name', type: 'string', required: true, maxLength: 40 },
      { name: 'email', type: 'email', required: true, maxLength: 254 },
      { name: 'registeredAt', type: 'date', required: false, format: 'yyyy-MM-dd' },
      { name: 'isActive', type: 'boolean', required: true },
    ],
  },
})

// Phase A: stop the Worker while the big file is being processed.
const bigJob = await upload('recovery-big.csv', bigCsv)
console.log(`big job: ${bigJob}`)
await waitFor(async () => {
  const current = await job(bigJob)
  return current.status === 'Processing' && current.processedRows > 0
}, 'big job to start processing', 120, 500)
console.log('Worker is processing the big file; stopping the worker container...')
docker('stop', '--timeout', stopTimeout, 'worker')
const stoppedAt = Date.now()

// Phase B: the API keeps accepting files while the Worker is down; the job
// stays Queued (outbox -> RabbitMQ queue) until the Worker returns.
const smallJob = await upload('recovery-small.csv', smallCsv)
console.log(`small job uploaded while Worker is down: ${smallJob}`)
await delay(3000)
const queuedWhileDown = await job(smallJob)
assert.equal(queuedWhileDown.status, 'Queued', `Expected the small job to stay Queued while the Worker is down, got ${queuedWhileDown.status}`)

docker('start', 'worker')
console.log('Worker restarted; waiting for both imports to finish...')
const big = await waitFor(async () => {
  const current = await job(bigJob)
  return current.completedAt ? current : null
})
const small = await waitFor(async () => {
  const current = await job(smallJob)
  return current.completedAt ? current : null
})

assert.ok(['Completed', 'CompletedWithErrors'].includes(big.status), `big job terminal status ${big.status}`)
assert.ok(['Completed', 'CompletedWithErrors'].includes(small.status), `small job terminal status ${small.status}`)
assert.equal(big.totalRows, bigRows)
assert.equal(big.processedRows, bigRows)
assert.equal(big.insertedRows, bigRows)
assert.equal(big.duplicateRows, 0)
assert.equal(small.insertedRows, smallValid)
assert.equal(small.invalidRows, smallInvalid)
assert.equal(small.duplicateRows, 0)
const recoverySeconds = Math.round((Date.now() - stoppedAt) / 100) / 10
console.log(JSON.stringify({
  result: 'passed',
  bigJob: { id: bigJob, rows: bigRows, status: big.status, inserted: big.insertedRows, duplicates: big.duplicateRows },
  smallJob: { id: smallJob, status: small.status, inserted: small.insertedRows, invalid: small.invalidRows },
  workerDowntimeSeconds: recoverySeconds,
}, null, 2))
