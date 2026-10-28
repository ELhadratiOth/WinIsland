// A stand-in for the Anthropic API, so the real Claude Code CLI can run a turn on a CI runner
// without a key: a request that offers tools gets one Bash call (`mkdir`, which asks for
// permission), one that already holds its result gets a short answer, anything else gets "ok".
// Every request goes into the log. A delay holds each answer that long, so a turn lasts long
// enough to be seen working.
//
//   node tools/fake-anthropic.js <port> <log file> [delay ms]
const http = require('http');
const fs = require('fs');

const [port, logFile, delay] = [Number(process.argv[2] || 8765), process.argv[3] || 'fake-anthropic.log', Number(process.argv[4] || 0)];
const say = (line) => fs.appendFileSync(logFile, `${new Date().toISOString()} ${line}\n`);

function reply(body) {
    const blocks = (body.messages || []).flatMap((m) => (Array.isArray(m.content) ? m.content : []));
    if (blocks.some((b) => b.type === 'tool_result')) {
        return [{ type: 'text', text: 'Done: the folder is there.' }];
    }
    if ((body.tools || []).some((t) => t.name === 'Bash')) {
        return [
            { type: 'text', text: 'Running echo.' },
            { type: 'tool_use', id: `toolu_${Date.now()}`, name: 'Bash', input: { command: 'mkdir winisland-check', description: 'Make a folder' } },
        ];
    }
    return [{ type: 'text', text: 'ok' }];
}

function message(body, blocks) {
    return {
        id: `msg_${Date.now()}`,
        type: 'message',
        role: 'assistant',
        model: body.model || 'claude-test',
        content: blocks,
        stop_reason: blocks.some((b) => b.type === 'tool_use') ? 'tool_use' : 'end_turn',
        stop_sequence: null,
        usage: { input_tokens: 10, output_tokens: 10, cache_creation_input_tokens: 0, cache_read_input_tokens: 0 },
    };
}

function stream(res, msg) {
    const send = (event, data) => res.write(`event: ${event}\ndata: ${JSON.stringify({ type: event, ...data })}\n\n`);
    res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' });
    send('message_start', { message: { ...msg, content: [], stop_reason: null, usage: { ...msg.usage, output_tokens: 1 } } });
    msg.content.forEach((block, index) => {
        if (block.type === 'text') {
            send('content_block_start', { index, content_block: { type: 'text', text: '' } });
            send('content_block_delta', { index, delta: { type: 'text_delta', text: block.text } });
        } else {
            send('content_block_start', { index, content_block: { ...block, input: {} } });
            send('content_block_delta', { index, delta: { type: 'input_json_delta', partial_json: JSON.stringify(block.input) } });
        }
        send('content_block_stop', { index });
    });
    send('message_delta', { delta: { stop_reason: msg.stop_reason, stop_sequence: null }, usage: { output_tokens: msg.usage.output_tokens } });
    send('message_stop', {});
    res.end();
}

http.createServer((req, res) => {
    let raw = '';
    req.on('data', (c) => (raw += c));
    req.on('end', () => {
        let body = {};
        try { body = raw ? JSON.parse(raw) : {}; } catch { /* not JSON */ }
        const path = req.url.split('?')[0];
        if (req.method === 'POST' && path.endsWith('/messages/count_tokens')) {
            say(`${req.method} ${path}`);
            res.writeHead(200, { 'content-type': 'application/json' });
            return res.end(JSON.stringify({ input_tokens: 10 }));
        }
        if (req.method === 'POST' && path.endsWith('/messages')) {
            const msg = message(body, reply(body));
            say(`${req.method} ${path} stream=${!!body.stream} tools=${(body.tools || []).length} -> ${msg.content.map((b) => b.type === 'tool_use' ? `tool_use ${b.name}` : 'text').join(', ')}`);
            return setTimeout(() => {
                if (body.stream) return stream(res, msg);
                res.writeHead(200, { 'content-type': 'application/json' });
                res.end(JSON.stringify(msg));
            }, delay);
        }
        say(`${req.method} ${path} (not handled)`);
        res.writeHead(404, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ type: 'error', error: { type: 'not_found_error', message: 'not here' } }));
    });
}).listen(port, '127.0.0.1', () => say(`listening on ${port}`));
