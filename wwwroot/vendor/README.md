# Vendored front-end assets

Third-party files served by the dashboard. They are checked in on purpose: the dashboard used to
load Chart.js from a CDN, which meant the whole page broke on a machine with no internet — an
odd failure mode for a tool whose entire job is to keep working when one upstream is unavailable.

| File | Version | License | Source |
|---|---|---|---|
| `chart.umd.min.js` | Chart.js 4.4.7 | MIT | `https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.min.js` |

## Verifying `chart.umd.min.js`

```bash
openssl dgst -sha384 -binary wwwroot/vendor/chart.umd.min.js | openssl base64 -A
# vsrfeLOOY6KuIYKDlmVH5UiBmgIdB1oEf7p01YgWHuqmOHfZr374+odEv96n9tNC
```

Recorded on 2026-07-31 against the file downloaded from the URL above. It is *not* used as an
`integrity` attribute in the page: the script is now served same-origin from this repository, so
subresource integrity would only be checking the file against itself. The hash is here so that a
future upgrade is a deliberate, reviewable change rather than a silent one.

## Upgrading

1. Download the new version to this directory.
2. Recompute the hash with the command above and update the table.
3. Load `/dashboard` and confirm all four charts render — the page deliberately try/catches each
   chart, so a broken upgrade degrades quietly instead of erroring.
