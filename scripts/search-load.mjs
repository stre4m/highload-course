import http from 'node:http'
import { performance } from 'node:perf_hooks'

const [baseUrl, concurrencyText, durationText, casesText] = process.argv.slice(2)
const concurrency = Number(concurrencyText)
const duration = Number(durationText) * 1000
const cases = JSON.parse(casesText)
const paths = cases.map(([first_name, last_name]) =>
  new URL('/user/search?' + new URLSearchParams({ first_name, last_name }), baseUrl))
const agent = new http.Agent({ keepAlive: true, maxSockets: concurrency })

function request(url) {
  return new Promise(resolve => {
    const req = http.get(url, { agent }, res => {
      res.on('error', () => resolve(false))
      res.on('end', () => resolve(res.complete && res.statusCode === 200))
      res.resume()
    })
    req.on('error', () => resolve(false))
    req.setTimeout(15000, () => req.destroy())
  })
}

async function run(milliseconds) {
  const latencies = []
  let errors = 0
  let next = 0
  const start = performance.now()
  const deadline = start + milliseconds
  await Promise.all(Array.from({ length: concurrency }, async () => {
    while (performance.now() < deadline) {
      const sent = performance.now()
      const ok = await request(paths[next++ % paths.length])
      if (ok) latencies.push(performance.now() - sent)
      else errors++
    }
  }))
  const seconds = (performance.now() - start) / 1000
  latencies.sort((a, b) => a - b)
  const percentile = p => latencies[Math.ceil(latencies.length * p) - 1] ?? null
  return {
    concurrency, seconds, successes: latencies.length, errors,
    rps: latencies.length / seconds,
    p50: percentile(.50), p95: percentile(.95), p99: percentile(.99),
  }
}

try {
  await run(3000)
  console.log(JSON.stringify(await run(duration)))
} finally {
  agent.destroy()
}
