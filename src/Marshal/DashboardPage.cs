namespace Marshal;

/// <summary>The self-contained dashboard page: inline CSS and a small script that polls the status and history JSON endpoints. No external assets, so it works on an isolated internal network</summary>
public static class DashboardPage
{
    /// <summary>The complete HTML document served at / and /dashboard</summary>
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>BugSwatter Dashboard</title>
        <style>
          :root { color-scheme: light dark; }
          body { font-family: system-ui, sans-serif; margin: 0; padding: 1.5rem; max-width: 1400px; margin: 0 auto; }
          h1 { font-size: 1.4rem; margin: 0 0 1rem; }
          .status { display: flex; gap: 1.5rem; flex-wrap: wrap; margin-bottom: 1.5rem; }
          .tile { border: 1px solid #8884; border-radius: 8px; padding: 0.75rem 1rem; min-width: 8rem; }
          .tile .label { font-size: 0.75rem; opacity: 0.7; text-transform: uppercase; letter-spacing: 0.05em; }
          .tile .value { font-size: 1.3rem; font-weight: 600; }
          .jobs { display: flex; flex-wrap: wrap; gap: 0.5rem; margin-bottom: 1.5rem; }
          .job { display: flex; align-items: center; gap: 0.4rem; border: 1px solid #8884; border-radius: 8px; padding: 0.3rem 0.6rem; }
          .job button { font: inherit; cursor: pointer; border: 1px solid #8886; border-radius: 6px; background: #8881; padding: 0.15rem 0.5rem; }
          table { width: 100%; border-collapse: collapse; font-size: 0.9rem; }
          th, td { text-align: left; padding: 0.4rem 0.6rem; border-bottom: 1px solid #8883; }
          th { font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.05em; opacity: 0.7; }
          .sev-Critical { color: #b00020; font-weight: 700; }
          .sev-High { color: #d1541f; font-weight: 600; }
          .sev-Medium { color: #b8860b; }
          .sev-Low, .sev-None { opacity: 0.7; }
          .out-completed { color: #1a7f37; }
          .out-failed, .out-timed-out, .out-aborted { color: #b00020; }
          footer { margin-top: 1.5rem; font-size: 0.8rem; opacity: 0.6; }
        </style>
        </head>
        <body>
        <h1>BugSwatter Dashboard</h1>
        <div class="status" id="status"></div>
        <div class="jobs" id="jobs"></div>
        <table>
          <thead><tr><th>Started</th><th>Job</th><th>Trigger</th><th>Outcome</th><th>Severity</th><th>Duration</th><th>Tokens</th><th>Estimated cost</th><th>Report</th></tr></thead>
          <tbody id="history"></tbody>
        </table>
        <footer id="footer"></footer>
        <script>
          function esc(s) { return (s ?? '').toString().replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
          function tile(label, value) { return '<div class="tile"><div class="label">' + label + '</div><div class="value">' + esc(value) + '</div></div>'; }
          function duration(seconds) {
            const value = Math.max(0, Math.floor(seconds || 0));
            const hours = Math.floor(value / 3600);
            const minutes = Math.floor((value % 3600) / 60);
            const remainder = value % 60;
            return (hours ? hours + 'h ' : '') + (hours || minutes ? minutes + 'm ' : '') + remainder + 's';
          }
          function reportedTokens(usage) {
            if (!usage) return 'not recorded';
            if (usage.totalTokens == null && usage.promptTokens == null && usage.completionTokens == null) return 'not reported';
            const total = usage.totalTokens == null ? '-' : usage.totalTokens.toLocaleString();
            const prompt = usage.promptTokens == null ? '-' : usage.promptTokens.toLocaleString();
            const completion = usage.completionTokens == null ? '-' : usage.completionTokens.toLocaleString();
            return total + ' total (' + prompt + ' input, ' + completion + ' output)';
          }
          function estimatedCost(usage) {
            if (!usage) return 'not recorded';
            if (usage.estimatedCost == null) return 'not calculated';
            return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(usage.estimatedCost);
          }
          function usageTiles(label, usage) {
            return tile(label + ' requests', usage?.requestCount ?? 0) + tile(label + ' tokens', reportedTokens(usage));
          }
          async function refresh() {
            try {
              const status = await (await fetch('/api/status')).json();
              const activity = status.activity;
              let statusHtml = tile('State', activity ? 'running' : 'idle');
              if (activity) {
                const started = new Date(activity.startedUtc);
                const model = activity.modelName ? activity.modelName + (activity.modelProfile ? ' (' + activity.modelProfile + ')' : '') : '-';
                const file = activity.currentFile ? (activity.fileIndex && activity.fileCount ? activity.fileIndex + '/' + activity.fileCount + ' - ' : '') + activity.currentFile : '-';
                const request = activity.modelRequestActive ? 'waiting for response' : 'between requests';
                statusHtml += tile('Job', activity.job) + tile('Trigger', activity.trigger) + tile('Phase', activity.phase) + tile('Started', started.toLocaleString()) +
                  tile('Elapsed', duration((Date.now() - started.getTime()) / 1000)) + tile('Model', model) + tile('Model request', request) +
                  usageTiles('Run', activity.runUsage) + usageTiles('Current phase/model', activity.currentUsage) + usageTiles('Local', activity.localUsage) +
                  usageTiles('Frontier', activity.frontierUsage) + tile('Estimated frontier cost', estimatedCost(activity.frontierUsage)) + tile('File', file);
                if (activity.modelRequestStartedUtc) {
                  const modelStarted = new Date(activity.modelRequestStartedUtc);
                  statusHtml += tile('Model request started', modelStarted.toLocaleString()) + tile('Model request elapsed', duration((Date.now() - modelStarted.getTime()) / 1000));
                }
              }
              statusHtml += tile('Queue depth', status.queueDepth) + tile('Jobs', status.jobCount) + tile('Uptime', duration(status.uptimeSeconds));
              document.getElementById('status').innerHTML = statusHtml;
              const jobs = await (await fetch('/api/jobs')).json();
              const queue = await (await fetch('/api/queue')).json();
              const waiting = new Set((queue.waiting || []).map(w => w.job));
              document.getElementById('jobs').innerHTML = jobs.map(j => {
                const state = queue.running === j.name ? ' (running)' : waiting.has(j.name) ? ' (queued)' : '';
                return '<div class="job"><span>' + esc(j.name) + state + '</span>' +
                  '<button data-job="' + esc(j.name) + '" data-action="run">Run now</button>' +
                  (waiting.has(j.name) ? '<button data-job="' + esc(j.name) + '" data-action="cancel">Cancel</button>' : '') + '</div>';
              }).join('');
              const history = await (await fetch('/api/history')).json();
              document.getElementById('history').innerHTML = history.map(h =>
                '<tr><td>' + esc((h.startedUtc ?? '').replace('T',' ').slice(0,19)) + '</td><td>' + esc(h.job) +
                '</td><td>' + esc(h.trigger) + '</td><td class="out-' + esc(h.outcome) + '">' + esc(h.outcome) +
                '</td><td class="sev-' + esc(h.maxSeverity ?? 'None') + '">' + esc(h.maxSeverity ?? '-') +
                '</td><td>' + esc(h.durationSeconds) + 's</td><td>' + esc(reportedTokens(h.runUsage)) +
                '</td><td>' + esc(estimatedCost(h.frontierUsage)) +
                '</td><td>' + esc((h.reportPath ?? '').split(/[\\/]/).pop()) + '</td></tr>').join('');
              document.getElementById('footer').textContent = 'Updated ' + new Date().toLocaleTimeString() + ' - ' + history.length + ' recent runs';
            } catch (e) {
              document.getElementById('footer').textContent = 'Could not reach BugSwatter: ' + e;
            }
          }
          document.getElementById('jobs').addEventListener('click', async e => {
            const btn = e.target.closest('button[data-job]');
            if (!btn) return;
            const name = btn.getAttribute('data-job');
            if (btn.getAttribute('data-action') === 'run') { await fetch('/api/jobs/' + encodeURIComponent(name) + '/run', { method: 'POST' }); }
            else { await fetch('/api/queue/' + encodeURIComponent(name), { method: 'DELETE' }); }
            refresh();
          });
          refresh();
          setInterval(refresh, 5000);
        </script>
        </body>
        </html>
        """;
}
