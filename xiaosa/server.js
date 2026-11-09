// FongMi 直播配置生成器 - 本地服务器
// 作用: 1) 托管生成器页面  2) 代理下载直播源（绕过浏览器 CORS 限制）
// 启动: node server.js   访问: http://127.0.0.1:8899
const http = require('http');
const https = require('https');
const fs = require('fs');
const path = require('path');

const PORT = 8899;
const ROOT = __dirname;

const MIME = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.css': 'text/css; charset=utf-8',
    '.png': 'image/png',
    '.jpg': 'image/jpeg',
    '.mp4': 'video/mp4'
};

const server = http.createServer((req, res) => {
    const u = new URL(req.url, 'http://localhost');

    // 代理下载接口: /fetch?url=<直播源地址>
    if (u.pathname === '/fetch') {
        const target = u.searchParams.get('url');
        if (!target || !/^https?:\/\//i.test(target)) {
            res.writeHead(400, { 'Content-Type': 'text/plain; charset=utf-8' });
            return res.end('参数错误: 需要 url=http(s)://...');
        }
        console.log('[proxy] ' + target);
        return download(target, res, 0);
    }

    // 静态文件
    let file = u.pathname === '/' ? '/live-config-generator.html' : decodeURIComponent(u.pathname);
    const fp = path.join(ROOT, path.normalize(file));
    if (!fp.startsWith(ROOT)) {
        res.writeHead(403);
        return res.end('forbidden');
    }
    fs.readFile(fp, (err, data) => {
        if (err) {
            res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
            return res.end('not found');
        }
        res.writeHead(200, { 'Content-Type': MIME[path.extname(fp).toLowerCase()] || 'application/octet-stream' });
        res.end(data);
    });
});

// 服务端下载（跟随重定向，最大 5 次）
function download(target, res, depth) {
    if (depth > 5) {
        res.writeHead(508, { 'Content-Type': 'text/plain; charset=utf-8' });
        return res.end('重定向次数过多');
    }
    const mod = target.toLowerCase().startsWith('https') ? https : http;
    const req = mod.get(target, {
        timeout: 30000,
        headers: { 'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)', 'Accept': '*/*' }
    }, r => {
        if (r.statusCode >= 300 && r.statusCode < 400 && r.headers.location) {
            r.resume();
            return download(new URL(r.headers.location, target).toString(), res, depth + 1);
        }
        const headers = {
            'Content-Type': r.headers['content-type'] || 'application/octet-stream',
            'Access-Control-Allow-Origin': '*'
        };
        if (r.headers['content-length']) headers['Content-Length'] = r.headers['content-length'];
        res.writeHead(r.statusCode, headers);
        r.pipe(res);
    });
    req.on('timeout', () => req.destroy(new Error('下载超时(30s)')));
    req.on('error', e => {
        console.error('[proxy] 失败: ' + e.message);
        if (!res.headersSent) res.writeHead(502, { 'Content-Type': 'text/plain; charset=utf-8' });
        res.end('下载失败: ' + e.message);
    });
}

server.listen(PORT, '0.0.0.0', () => {
    console.log(`服务器已启动: http://127.0.0.1:${PORT}`);
    console.log('按 Ctrl+C 停止');
});
