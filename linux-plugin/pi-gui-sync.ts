import type { ExtensionAPI, ExtensionContext } from "@earendil-works/pi-coding-agent";
import { createServer, type Server, type ServerResponse } from "node:http";
import { randomBytes } from "node:crypto";

const MAX_BODY = 12 * 1024 * 1024;
type SyncServer = { server: Server; token: string; port: number; clients: Set<ServerResponse> };

function writeSse(client: ServerResponse, message: unknown): void {
  client.write(`data: ${JSON.stringify(message)}\n\n`);
}

async function requestBody(req: AsyncIterable<Buffer | string>): Promise<string> {
  const chunks: Buffer[] = []; let size = 0;
  for await (const item of req) {
    const data = Buffer.isBuffer(item) ? item : Buffer.from(item); size += data.length;
    if (size > MAX_BODY) throw new Error("请求超过 12 MiB 限制");
    chunks.push(data);
  }
  return Buffer.concat(chunks).toString("utf8");
}

export default function (pi: ExtensionAPI) {
  let sync: SyncServer | undefined;
  let sessionContext: ExtensionContext | undefined;

  const broadcast = (message: unknown) => {
    if (!sync) return;
    for (const client of sync.clients) writeSse(client, message);
  };
  const stop = async () => {
    if (!sync) return;
    const current = sync; sync = undefined;
    for (const client of current.clients) client.end();
    await new Promise<void>((resolve) => current.server.close(() => resolve()));
  };

  pi.on("session_start", (_event, ctx) => { sessionContext = ctx; });
  pi.on("message_start", (event) => broadcast({ type: "message_start", message: event.message }));
  pi.on("message_update", (event) => broadcast({ type: "message_update", assistantMessageEvent: event.assistantMessageEvent }));
  pi.on("message_end", (event) => broadcast({ type: "message_end", message: event.message }));
  pi.on("agent_start", () => broadcast({ type: "agent_start" }));
  pi.on("agent_settled", () => broadcast({ type: "agent_settled" }));
  pi.on("turn_start", (event) => broadcast({ type: "turn_start", turnIndex: event.turnIndex }));
  pi.on("turn_end", (event) => broadcast({ type: "turn_end", toolResults: event.toolResults }));
  pi.on("tool_execution_start", (event) => broadcast({ type: "tool_execution_start", toolCallId: event.toolCallId, toolName: event.toolName, args: event.args }));
  pi.on("tool_execution_update", (event) => broadcast({ type: "tool_execution_update", toolCallId: event.toolCallId, toolName: event.toolName, partialResult: event.partialResult }));
  pi.on("tool_execution_end", (event) => broadcast({ type: "tool_execution_end", toolCallId: event.toolCallId, toolName: event.toolName, result: event.result, isError: event.isError }));
  pi.on("queue_update", (event) => broadcast({ type: "queue_update", steering: event.steering, followUp: event.followUp }));
  pi.on("compaction_start", (event) => broadcast({ type: "compaction_start", reason: event.reason }));
  pi.on("compaction_end", (event) => broadcast({ type: "compaction_end", reason: event.reason, aborted: event.aborted }));
  pi.on("auto_retry_start", (event) => broadcast({ type: "auto_retry_start", attempt: event.attempt, maxAttempts: event.maxAttempts, errorMessage: event.errorMessage }));
  pi.on("auto_retry_end", (event) => broadcast({ type: "auto_retry_end", success: event.success, attempt: event.attempt }));

  pi.registerCommand("gui-sync", {
    description: "将当前 CLI Pi 会话实时同步给 Windows GUI：/gui-sync start [端口] | stop | status",
    handler: async (args, ctx) => {
      const [action = "start", rawPort] = args.trim().split(/\s+/, 2);
      if (action === "stop") { await stop(); ctx.ui.notify("GUI 实时同步已停止", "info"); return; }
      if (action === "status") { ctx.ui.notify(sync ? `GUI 同步运行在 127.0.0.1:${sync.port}` : "GUI 同步未启动", "info"); return; }
      if (action !== "start") { ctx.ui.notify("用法：/gui-sync start [端口] | stop | status", "error"); return; }
      if (sync) { ctx.ui.notify(`GUI 同步已运行：127.0.0.1:${sync.port}`, "info"); return; }
      const port = rawPort ? Number(rawPort) : 18765;
      if (!Number.isInteger(port) || port < 1024 || port > 65535) { ctx.ui.notify("端口必须介于 1024-65535", "error"); return; }
      const token = randomBytes(32).toString("hex");
      const clients = new Set<ServerResponse>();
      const server = createServer(async (req, res) => {
        const url = new URL(req.url ?? "/", "http://127.0.0.1");
        const valid = url.searchParams.get("token") === token || req.headers["x-pi-gui-token"] === token;
        if (!valid) { res.writeHead(401).end("invalid token"); return; }
        if (req.method === "GET" && url.pathname === "/events") {
          res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-cache", connection: "keep-alive" });
          clients.add(res); writeSse(res, { type: "connected", sessionFile: sessionContext?.sessionManager.getSessionFile() });
          req.on("close", () => clients.delete(res)); return;
        }
        if (req.method === "GET" && url.pathname === "/commands") {
          res.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ commands: pi.getCommands() }));
          return;
        }
        if (req.method === "GET" && url.pathname === "/history") {
          const entries = sessionContext?.sessionManager.getBranch() ?? [];
          const messages = entries.filter((entry: any) => entry.type === "message").map((entry: any) => entry.message);
          res.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ messages }));
          return;
        }
        if (req.method === "POST" && url.pathname === "/abort") {
          sessionContext?.abort();
          res.writeHead(202, { "content-type": "application/json" }).end('{"accepted":true}');
          return;
        }
        if (req.method === "POST" && url.pathname === "/prompt") {
          try {
            const data = JSON.parse(await requestBody(req));
            const text = typeof data.message === "string" ? data.message : "";
            const image = data.image;
            if (!text.trim() && !image) throw new Error("需要文字或图片");
            const content: Array<{ type: "text"; text: string } | { type: "image"; source: { type: "base64"; mediaType: string; data: string } }> = [];
            if (text.trim()) content.push({ type: "text", text });
            if (image?.data && typeof image.data === "string" && ["image/png", "image/jpeg", "image/webp"].includes(image.mimeType))
              content.push({ type: "image", source: { type: "base64", mediaType: image.mimeType, data: image.data } });
            pi.sendUserMessage(content, { deliverAs: "followUp", expandPromptTemplates: true });
            res.writeHead(202, { "content-type": "application/json" }).end('{"accepted":true}');
          } catch (error) { res.writeHead(400, { "content-type": "application/json" }).end(JSON.stringify({ error: error instanceof Error ? error.message : "invalid request" })); }
          return;
        }
        res.writeHead(404).end();
      });
      try {
        await new Promise<void>((resolve, reject) => { server.once("error", reject); server.listen(port, "127.0.0.1", () => resolve()); });
      } catch (error) { ctx.ui.notify(`无法启动 GUI 同步：${error instanceof Error ? error.message : error}`, "error"); return; }
      sync = { server, token, port, clients };
      ctx.ui.notify(`GUI 同步已启动。Windows SSH 隧道：-L ${port}:127.0.0.1:${port}；令牌：${token}`, "info");
    },
  });
  pi.on("session_shutdown", async () => { await stop(); });
}
