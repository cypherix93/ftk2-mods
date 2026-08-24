using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using FTK2Mods.Crucible;

namespace Crucible.Plugin
{
    /// <summary>
    /// Loopback-only HTTP control surface.
    ///
    /// Binds 127.0.0.1 explicitly — never a wildcard prefix, which on Windows would demand an admin
    /// URL ACL and would expose a remote-control channel for the game to the whole LAN.
    /// </summary>
    internal sealed class RpcServer
    {
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private readonly int _port;
        private readonly string _token;

        internal RpcServer(int port, string token)
        {
            _port = port;
            _token = token;
        }

        internal int Port { get { return _port; } }

        internal void Start()
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
                _listener.Start();
                _running = true;
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Name = "crucible-rpc";
                _thread.Start();

                // Don't take Start() at its word. Under Unity's Mono runtime HttpListener is a managed
                // implementation, so a successful Start() should show up as a real TCP listener — and when
                // it doesn't, the old unconditional "RPC listening" line sent whoever was debugging to
                // entirely the wrong place. Assert the socket actually exists and say so either way.
                if (IsPortListening(_port))
                {
                    CruciblePlugin.Log("RPC listening on http://127.0.0.1:" + _port + "/");
                }
                else
                {
                    CruciblePlugin.Log("RPC WARNING: HttpListener.Start() returned without error but no TCP "
                        + "listener is bound on 127.0.0.1:" + _port + ". The control surface is NOT reachable. "
                        + "If a urlacl is required on this machine, reserve it with: netsh http add urlacl "
                        + "url=http://127.0.0.1:" + _port + "/ user=" + Environment.UserName);
                }
            }
            catch (Exception ex)
            {
                CruciblePlugin.Log("RPC failed to start on port " + _port + ": " + ex.Message
                    + " (if a second game instance is running, set " + CruciblePlugin.EnvPort + " to a free port)");
            }
        }

        internal void Stop()
        {
            // Logged because a silent teardown is indistinguishable from a listener that never bound —
            // if something destroys the plugin on a scene load, this line is the only way to tell.
            CruciblePlugin.Log("RPC stopping on port " + _port + ".");
            _running = false;
            try { if (_listener != null) _listener.Stop(); }
            catch (Exception) { }
        }

        /// <summary>
        /// True when the OS reports a bound TCP listener on the loopback port. Pure inspection — it does
        /// not open a connection, so it cannot leave a half-formed request in the listener's queue.
        /// </summary>
        private static bool IsPortListening(int port)
        {
            try
            {
                IPEndPoint[] listeners = System.Net.NetworkInformation.IPGlobalProperties
                    .GetIPGlobalProperties().GetActiveTcpListeners();
                for (int i = 0; i < listeners.Length; i++)
                {
                    if (listeners[i].Port == port) return true;
                }
                return false;
            }
            catch
            {
                // If the platform won't answer, don't claim a failure we can't prove.
                return true;
            }
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx = null;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { if (!_running) return; continue; }

                try { Handle(ctx); }
                catch (Exception ex)
                {
                    try { Respond(ctx, 500, Error("handler_failed: " + ex.Message)); }
                    catch (Exception) { }
                }
            }
        }

        private static Dictionary<string, object> Error(string message)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = false;
            d["error"] = message;
            return d;
        }

        private void Handle(HttpListenerContext ctx)
        {
            if (!string.IsNullOrEmpty(_token))
            {
                string provided = ctx.Request.Headers["X-Crucible-Token"];
                if (!string.Equals(provided, _token, StringComparison.Ordinal))
                {
                    Respond(ctx, 401, Error("unauthorized"));
                    return;
                }
            }

            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            if (path.Length == 0) path = "/";
            string body = ReadBody(ctx);

            switch (path)
            {
                case "/":
                case "/health": HandleHealth(ctx); return;
                case "/commands": HandleCommands(ctx); return;
                case "/exec": HandleExec(ctx, body); return;
                case "/state": HandleState(ctx); return;
                case "/screenshot": HandleScreenshot(ctx, body); return;
                case "/trace": HandleTrace(ctx); return;
                default: Respond(ctx, 404, Error("unknown_endpoint")); return;
            }
        }

        private static string ReadBody(HttpListenerContext ctx)
        {
            if (!ctx.Request.HasEntityBody) return null;
            using (StreamReader reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private void HandleHealth(HttpListenerContext ctx)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["version"] = "0.1.0";
            d["instance"] = CruciblePlugin.Instance.InstanceName;
            d["port"] = _port;
            d["bridgeAvailable"] = GameBridge.IsAvailable;

            object result; string error;
            bool pumped = MainThreadPump.Run(delegate { return GameBridge.IsOnlineSession(); }, 3000, out result, out error);
            d["online"] = result is bool && (bool)result;
            d["pumpAlive"] = pumped;
            if (!pumped) d["pumpError"] = error;

            Respond(ctx, 200, d);
        }

        private void HandleCommands(HttpListenerContext ctx)
        {
            object result; string error;
            if (!MainThreadPump.Run(delegate { return GameBridge.ListCommands(); }, 5000, out result, out error))
            {
                Respond(ctx, 503, Error(error));
                return;
            }

            List<object> list = new List<object>();
            string[] commands = result as string[];
            if (commands != null) for (int i = 0; i < commands.Length; i++) list.Add(commands[i]);

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["count"] = list.Count;
            d["commands"] = list;
            Respond(ctx, 200, d);
        }

        private void HandleExec(HttpListenerContext ctx, string body)
        {
            if (body == null) { Respond(ctx, 400, Error("missing_body")); return; }

            object parsed; string parseError;
            if (!MiniJson.TryParse(body, out parsed, out parseError))
            {
                Respond(ctx, 400, Error("bad_json: " + parseError));
                return;
            }

            Dictionary<string, object> req = MiniJson.AsObject(parsed);
            if (req == null) { Respond(ctx, 400, Error("body_not_an_object")); return; }

            string command = MiniJson.AsString(req.ContainsKey("command") ? req["command"] : null);
            if (string.IsNullOrEmpty(command)) { Respond(ctx, 400, Error("missing_command")); return; }

            string correlationId = MiniJson.AsString(req.ContainsKey("correlationId") ? req["correlationId"] : null);
            if (string.IsNullOrEmpty(correlationId)) correlationId = Guid.NewGuid().ToString("N");

            List<object> rawArgs = req.ContainsKey("args") ? MiniJson.AsArray(req["args"]) : null;
            List<string> args = new List<string>();
            if (rawArgs != null)
            {
                for (int i = 0; i < rawArgs.Count; i++)
                {
                    string s = MiniJson.AsString(rawArgs[i]);
                    // Numbers arrive as JsonNumber; the game marshals from strings anyway.
                    args.Add(s != null ? s : Convert.ToString(rawArgs[i]));
                }
            }

            object onlineResult; string pumpProbeError;
            MainThreadPump.Run(delegate { return GameBridge.IsOnlineSession(); }, 3000, out onlineResult, out pumpProbeError);
            bool online = onlineResult is bool && (bool)onlineResult;

            GateVerdict verdict = CommandGate.Evaluate(command, online,
                CruciblePlugin.Instance.CfgAllowMutationsInMP.Value, CruciblePlugin.Instance.Allowlist);

            if (verdict != GateVerdict.Allow)
            {
                string reason = verdict == GateVerdict.DeniedMultiplayer ? "denied_multiplayer" : "denied_not_allowlisted";
                WriteTrace("exec_denied", correlationId, command, args, reason, 0);
                Respond(ctx, 403, Error(reason));
                return;
            }

            string[] argArray = args.ToArray();
            Stopwatch sw = Stopwatch.StartNew();
            object result; string pumpError; string reflectiveResult = null;
            bool ok = MainThreadPump.Run(delegate
            {
                string inner;
                // Cleared before dispatch so a command that doesn't touch one of these (e.g. a
                // crucible_ui_* call leaving ReflectionCommands.LastResult untouched) can't leak the
                // previous, unrelated command's stashed output back out through this request.
                ReflectionCommands.LastResult = null;
                UiCommands.LastResult = null;
                DebugVerbCommands.LastResult = null;
                ChaosCommands.LastResult = null;
                InputFocusGateCommands.LastResult = null;
                MouseCommands.LastResult = null;
                GamepadCommands.LastResult = null;
                KeyboardCommands.LastResult = null;
                InputBackgroundCommands.LastResult = null;
                FixtureCommands.LastResult = null;
                AbilityCommands.LastResult = null;
                ConfigCommands.LastResult = null;
                CombatCommands.LastResult = null;
                DebugSpawnCommands.LastResult = null;
                CharacterCommands.LastResult = null;
                CombatDriveCommands.LastResult = null;
                OverworldCommands.LastResult = null;
                RunCommands.LastResult = null;
                RunEndCommands.LastResult = null;
                bool success = GameBridge.Exec(command, argArray, out inner);
                // crucible_get/crucible_invoke/crucible_ui_*/crucible debug-verb commands stash their
                // rendered output here rather than returning it through ExecuteCommand, which reports
                // dispatch, not data. Reading it in the same work item is safe: MainThreadPump
                // serializes all game-thread work, so nothing else can run between the handler
                // returning and this line.
                reflectiveResult = ReflectionCommands.LastResult ?? UiCommands.LastResult
                    ?? DebugVerbCommands.LastResult ?? ChaosCommands.LastResult
                    ?? InputFocusGateCommands.LastResult ?? MouseCommands.LastResult
                    ?? GamepadCommands.LastResult ?? KeyboardCommands.LastResult
                    ?? InputBackgroundCommands.LastResult ?? FixtureCommands.LastResult ?? AbilityCommands.LastResult ?? ConfigCommands.LastResult ?? CombatCommands.LastResult ?? DebugSpawnCommands.LastResult ?? CharacterCommands.LastResult ?? CombatDriveCommands.LastResult ?? OverworldCommands.LastResult ?? RunCommands.LastResult ?? RunEndCommands.LastResult;
                return success ? (object)true : (object)inner;
            }, 10000, out result, out pumpError);
            sw.Stop();

            string execError = null;
            if (!ok) execError = pumpError;
            else if (!(result is bool)) { execError = result as string; ok = false; }

            Dictionary<string, object> response = new Dictionary<string, object>();
            response["ok"] = ok;
            if (execError != null) response["error"] = execError;
            response["command"] = command;
            response["durationMs"] = (int)sw.ElapsedMilliseconds;
            response["correlationId"] = correlationId;
            if (ok && reflectiveResult != null) response["result"] = reflectiveResult;

            WriteTrace(ok ? "exec" : "exec_failed", correlationId, command, args, execError, (int)sw.ElapsedMilliseconds, reflectiveResult);
            Respond(ctx, ok ? 200 : 400, response);
        }

        private static void WriteTrace(string kind, string correlationId, string command, List<string> args, string error, int durationMs, string result = null)
        {
            if (CruciblePlugin.Trace == null) return;

            Dictionary<string, object> fields = new Dictionary<string, object>();
            fields["command"] = command;
            List<object> argList = new List<object>();
            if (args != null) for (int i = 0; i < args.Count; i++) argList.Add(args[i]);
            fields["args"] = argList;
            fields["durationMs"] = durationMs;
            fields["instance"] = CruciblePlugin.Instance.InstanceName;
            if (error != null) fields["error"] = error;
            if (result != null) fields["result"] = result;

            CruciblePlugin.Trace.Write(kind, correlationId, fields);
        }

        private void HandleState(HttpListenerContext ctx)
        {
            // Routing happens on AbsolutePath, which excludes the query string, so v1 keeps its
            // exact behaviour and v2 is purely additive.
            string schema = ctx.Request.QueryString["schema"];
            bool v2 = string.Equals(schema, "v2", StringComparison.OrdinalIgnoreCase);

            object result;
            string error;
            bool pumped;
            if (v2)
            {
                pumped = MainThreadPump.Run(delegate { return StateReaderV2.Snapshot(); }, 5000, out result, out error);
            }
            else
            {
                pumped = MainThreadPump.Run(delegate { return StateReader.Snapshot(); }, 5000, out result, out error);
            }

            if (!pumped)
            {
                Respond(ctx, 503, Error(error));
                return;
            }

            Dictionary<string, object> snapshot = result as Dictionary<string, object>;

            // The instance name is part of the snapshot for readability, but it MUST NOT feed the
            // digest — otherwise two healthy peers would never agree and every comparison would
            // report a desync. v2 redacts more: entity guids and the episode key are per-process
            // runtime values, and one peer noticing a reflection miss is not a desync.
            string[] redactions = v2 ? Redactions.CopyV2() : new string[] { "instance" };

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["instance"] = CruciblePlugin.Instance.InstanceName;
            d["snapshot"] = snapshot;
            d["digest"] = StateDigest.Compute(snapshot, redactions);
            Respond(ctx, 200, d);
        }

        private void HandleScreenshot(HttpListenerContext ctx, string body)
        {
            string label = null;
            if (!string.IsNullOrEmpty(body))
            {
                object parsed; string parseError;
                if (MiniJson.TryParse(body, out parsed, out parseError))
                {
                    Dictionary<string, object> req = MiniJson.AsObject(parsed);
                    if (req != null && req.ContainsKey("label")) label = MiniJson.AsString(req["label"]);
                }
            }

            string path, error;
            if (!CaptureService.CaptureBlocking(label, 10000, out path, out error))
            {
                Respond(ctx, 503, Error(error));
                return;
            }

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["instance"] = CruciblePlugin.Instance.InstanceName;
            d["path"] = path;
            try { d["base64"] = Convert.ToBase64String(File.ReadAllBytes(path)); }
            catch (Exception ex) { d["base64"] = null; d["readError"] = ex.Message; }

            Respond(ctx, 200, d);
        }

        private void HandleTrace(HttpListenerContext ctx)
        {
            int n = 50;
            string raw = ctx.Request.QueryString["n"];
            if (!string.IsNullOrEmpty(raw)) int.TryParse(raw, out n);
            if (n < 1) n = 1;

            List<object> lines = new List<object>();
            if (CruciblePlugin.Trace != null)
            {
                string[] tail = CruciblePlugin.Trace.Tail(n);
                for (int i = 0; i < tail.Length; i++) lines.Add(tail[i]);
            }

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["instance"] = CruciblePlugin.Instance.InstanceName;
            d["path"] = CruciblePlugin.Trace != null ? CruciblePlugin.Trace.FilePath : null;
            d["lines"] = lines;
            Respond(ctx, 200, d);
        }

        private static void Respond(HttpListenerContext ctx, int status, Dictionary<string, object> payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(MiniJson.Write(payload));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
