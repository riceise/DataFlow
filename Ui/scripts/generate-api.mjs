import fs from 'node:fs/promises'
import openapiTS, { astToString } from 'openapi-typescript'

const url = process.env.OPENAPI_URL ?? 'http://localhost:5030/openapi/v1.json'
const schema = await (await fetch(url)).json()
// System.Text.Json accepts numeric strings on input, but responses emit numbers.
// Generate response numbers without widening all UI arithmetic to number|string.
function normalize(value) {
  if (!value || typeof value !== 'object') return
  if (
    (Array.isArray(value.type) && value.type.includes('number')) ||
    (Array.isArray(value.type) && value.type.includes('integer'))
  ) {
    value.type = value.type.filter((type) => type !== 'string')
  }
  for (const child of Object.values(value)) {
    if (Array.isArray(child)) child.forEach(normalize)
    else normalize(child)
  }
}
normalize(schema)
await fs.writeFile(
  new URL('../src/api-schema.d.ts', import.meta.url),
  astToString(await openapiTS(schema)),
)
console.log(`API types generated from ${url}`)
