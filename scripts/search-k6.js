import http from 'k6/http'
import exec from 'k6/execution'
import { Counter, Trend } from 'k6/metrics'

const concurrency = Number(__ENV.SEARCH_VUS)
const duration = Number(__ENV.SEARCH_DURATION)
const rampSeconds = 5
const warmupSeconds = 3
const cases = JSON.parse(__ENV.SEARCH_CASES)
const urls = cases.map(([first, last]) =>
  __ENV.SEARCH_URL + '/user/search?first_name=' + encodeURIComponent(first)
  + '&last_name=' + encodeURIComponent(last))

const attempts = new Counter('search_attempts')
const successes = new Counter('search_successes')
const errors = new Counter('search_errors')
const latency = new Trend('search_latency', true)
const completion = new Trend('search_completion', true)

export const options = {
  discardResponseBodies: true,
  summaryTrendStats: ['med', 'p(95)', 'p(99)', 'max'],
  scenarios: {
    search: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: rampSeconds + 's', target: concurrency },
        { duration: (warmupSeconds + duration) + 's', target: concurrency },
      ],
      gracefulStop: '16s',
    },
  },
}

export default function () {
  const url = urls[exec.scenario.iterationInTest % urls.length]
  const windowStart = exec.scenario.startTime + (rampSeconds + warmupSeconds) * 1000
  const start = Date.now()
  const measured = start >= windowStart && start < windowStart + duration * 1000
  if (measured) attempts.add(1)
  const response = http.get(url, { redirects: 0, timeout: '15s', tags: { name: 'GET /user/search' } })
  if (!measured) return
  if (response.status === 200 && !response.error_code) {
    successes.add(1)
    latency.add(response.timings.duration)
  } else {
    errors.add(1)
  }
  completion.add(Date.now() - windowStart)
}

export function handleSummary(data) {
  return { stdout: JSON.stringify(data) + '\n' }
}
