// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

const fs = require('fs');
const path = require('path');

const marker = '<!-- transport-performance-comparison -->';
const stacks = ['tcp-noise-yamux', 'tcp-tls-yamux', 'quic-v1'];
const metrics = ['upload', 'download', 'latency'];

function median(values) {
    const ordered = [...values].sort((a, b) => a - b);
    return ordered[Math.floor(ordered.length / 2)];
}

function readMeasurement(stack, revision, metric, sample) {
    const filename = `${stack}-${revision}-${metric}-${sample}.json`;
    try {
        const result = JSON.parse(fs.readFileSync(path.join('performance-results', filename), 'utf8'));
        const unit = metric === 'latency' ? 'ms' : 'MiB/s';
        if (result.stack !== stack || result.metric !== metric || result.unit !== unit ||
            !Number.isFinite(result.median) || result.median <= 0 ||
            !Array.isArray(result.samples) || result.samples.length !== 1 ||
            result.samples[0] !== result.median) {
            return null;
        }
        return result.median;
    } catch {
        return null;
    }
}

function reportTable() {
    const rows = [
        '| Stack | Measure | main | PR | PR vs main |',
        '| --- | --- | ---: | ---: | ---: |',
    ];
    for (const stack of stacks) {
        for (const metric of metrics) {
            const values = {};
            const display = {};
            const unit = metric === 'latency' ? 'ms' : 'MiB/s';
            for (const revision of ['base', 'head']) {
                const samples = [1, 2, 3, 4, 5].map(sample =>
                    readMeasurement(stack, revision, metric, sample)).filter(value => value !== null);
                values[revision] = samples.length === 5 ? median(samples) : null;
                display[revision] = samples.length === 5
                    ? `${values[revision].toFixed(2)} ${unit}`
                    : `incomplete (${samples.length}/5)`;
            }
            const change = values.base === null || values.head === null
                ? '—'
                : `${values.head >= values.base ? '+' : ''}${((values.head / values.base - 1) * 100).toFixed(1)}%`;
            rows.push(`| ${stack} | ${metric} | ${display.base} | ${display.head} | ${change} |`);
        }
    }
    return rows.join('\n');
}

module.exports = async ({ github, context, core }) => {
    const run = context.payload.workflow_run;
    if (run.path !== '.github/workflows/performance.yml') {
        return;
    }
    const { owner, repo } = context.repo;
    let candidates = run.pull_requests || [];
    if (candidates.length === 0) {
        const openPulls = await github.paginate(github.rest.pulls.list, { owner, repo, state: 'open' });
        candidates = openPulls.filter(pr => pr.head.ref === run.head_branch &&
            pr.head.repo?.full_name === run.head_repository?.full_name);
    }
    if (candidates.length !== 1) {
        core.info('No unique pull request is associated with this benchmark run.');
        return;
    }

    const pullNumber = candidates[0].number;
    const { data: pull } = await github.rest.pulls.get({ owner, repo, pull_number: pullNumber });
    if (pull.state !== 'open' || !pull.labels.some(label => label.name === 'performance is good')) {
        return;
    }
    if (pull.head.repo?.full_name !== run.head_repository?.full_name) {
        return;
    }
    if (candidates[0].head?.sha && candidates[0].head.sha !== pull.head.sha) {
        core.info('The pull request has a newer head; skipping the stale report.');
        return;
    }

    const runLink = `https://github.com/${owner}/${repo}/actions/runs/${run.id}`;
    let body = `${marker}\n## Transport performance compared with main\n\n`;
    if (run.conclusion === 'success') {
        try {
            body += `${reportTable()}\n\n`;
            body += 'Each number is the median of five independent runs on the same Ubuntu runner. '
                + 'Upload and download use 32 KiB transfers; latency uses a one byte round trip. '
                + 'An incomplete result means at least one run failed or timed out. '
                + 'Higher throughput and lower latency are better. Hosted runner variation makes these advisory measurements.\n\n';
        } catch {
            body += 'The measurement artifacts were incomplete or invalid. See the workflow run for details.\n\n';
        }
    } else {
        body += 'The comparison did not complete. See the workflow run for the failing stack or build.\n\n';
    }
    body += `[Workflow run](${runLink})`;

    const comments = await github.paginate(github.rest.issues.listComments,
        { owner, repo, issue_number: pullNumber });
    const previous = comments.find(comment => comment.user?.type === 'Bot' &&
        comment.body?.startsWith(marker));
    if (previous) {
        await github.rest.issues.updateComment({ owner, repo, comment_id: previous.id, body });
    } else {
        await github.rest.issues.createComment({ owner, repo, issue_number: pullNumber, body });
    }
};
