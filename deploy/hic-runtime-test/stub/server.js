const http = require('node:http');

function respond(response, status, payload) {
    const content = JSON.stringify(payload);
    response.writeHead(status, {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(content),
    });
    response.end(content);
}

const server = http.createServer((request, response) => {
    if (request.method === 'GET' && request.url === '/health') {
        respond(response, 200, { status: 'ok' });
        return;
    }
    if (request.method !== 'POST' ||
        !['/v1/chat/completions', '/v1/completions'].includes(request.url)) {
        respond(response, 404, { error: 'Unknown route' });
        request.resume();
        return;
    }

    const chunks = [];
    let length = 0;
    request.on('data', chunk => {
        length += chunk.length;
        if (response.writableEnded) return;
        if (length > 65536) {
            chunks.length = 0;
            respond(response, 413, { error: 'Request too large' });
            return;
        }
        chunks.push(chunk);
    });
    request.on('end', () => {
        if (response.writableEnded) return;
        let payload;
        try {
            payload = JSON.parse(Buffer.concat(chunks).toString('utf8'));
        } catch (error) {
            if (!(error instanceof SyntaxError)) throw error;
            respond(response, 400, { error: 'Invalid JSON request' });
            return;
        }
        if (!payload || payload.model !== 'hic-deterministic-stub') {
            respond(response, 400, { error: 'Unsupported model' });
            return;
        }
        respond(response, 200, {
            choices: [{
                message: { content: 'HIC_RUNTIME_TEST_OK' },
                text: 'HIC_RUNTIME_TEST_OK',
            }],
        });
    });
    request.on('error', () => {
        if (!response.writableEnded) {
            respond(response, 400, { error: 'Request stream error' });
        }
    });
});

server.requestTimeout = 10000;
server.headersTimeout = 10000;
server.listen(8000, '0.0.0.0');
