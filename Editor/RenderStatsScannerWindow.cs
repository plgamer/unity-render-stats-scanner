using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace RenderStatsScanner
{
    /// <summary>
    /// 渲染统计异常抓拍器:Play 模式下持续监控 UnityStats(Stats 面板同源数据),
    /// 发现 batches 等指标越过绝对阈值、或相对近期基线突增时自动截图并记录当帧全套 stats。
    /// 采样数据实时落盘,因此退出 Play(domain reload)不会丢数据;退出后自动生成
    /// 可离线查看的 HTML 报告(截图 + 当时的 stats),窗口内也可回看历史报告。
    ///
    /// 产物目录:Logs/RenderStatsScanner/session_yyyyMMdd_HHmmss/
    ///   events.tsv  异常事件明细(也是窗口回读的数据源)
    ///   summary.tsv 本次采样概览
    ///   report.html 自包含报告,双击即可看
    ///   shots/      截图
    /// </summary>
    public class RenderStatsScannerWindow : EditorWindow
    {
        // ── 默认阈值:移动端三消常见参考值,可在窗口里改,改动会记进 EditorPrefs ──
        private const int DEFAULT_BATCHES_THRESHOLD = 150;
        private const int DEFAULT_SETPASS_THRESHOLD = 80;
        private const int DEFAULT_DRAWCALLS_THRESHOLD = 200;
        private const int DEFAULT_TRIANGLES_THRESHOLD = 200000;
        private const int DEFAULT_VERTICES_THRESHOLD = 200000;
        private const float DEFAULT_SNAPSHOT_COOLDOWN = 1.0f;
        private const float DEFAULT_SPIKE_RATIO = 1.5f;
        private const int DEFAULT_SPIKE_MIN_DELTA = 30;

        private const string ROOT_DIR = "Logs/RenderStatsScanner";
        private const string SHOTS_SUBDIR = "shots";

        // 基线窗口:用最近 N 帧的中位数当"正常水位",中位数比均值更抗自身尖峰污染
        private const int BASELINE_WINDOW = 60;
        // 截图异步落盘,报告要等文件写完再生成
        private const double HTML_DELAY_SEC = 1.5;

        private const string PREF_PREFIX = "RenderStatsScanner.";
        private const string SS_SESSION_DIR = "RenderStatsScanner.SessionDir";
        private const string SS_PENDING_HTML_AT = "RenderStatsScanner.PendingHtmlAt";

        // ── 阈值配置 ──
        private int _batchesThreshold;
        private int _setPassThreshold;
        private int _drawCallsThreshold;
        private int _trianglesThreshold;
        private int _verticesThreshold;
        private float _snapshotCooldownSec;
        private bool _spikeEnabled;
        private float _spikeRatio;
        private int _spikeMinDelta;

        // ── 采样状态 ──
        private bool _isSampling;
        private double _sampleStartTime;
        private int _sampledFrameCount;
        private int _lastRenderedFrame = -1;
        private string _sessionDir;
        private StreamWriter _eventWriter;
        private int _shotIndex;
        private double _lastSnapshotTime = -1.0;

        private Frame _cur;

        // 聚合
        private int _maxBatches, _maxSetPass, _maxDrawCalls, _maxTriangles, _maxVertices;
        private long _sumBatches, _sumSetPass, _sumDrawCalls, _sumTriangles, _sumVertices;

        // 基线环形缓冲
        private readonly int[] _baselineBuf = new int[BASELINE_WINDOW];
        private int _baselineCount;
        private int _baselineHead;

        // ── 展示状态(全部可从磁盘重建,domain reload 后自动恢复)──
        private string _loadedSessionDir;
        private readonly List<AnomalyEvent> _events = new List<AnomalyEvent>();
        private readonly List<string> _summaryLines = new List<string>();
        private readonly Dictionary<string, Texture2D> _thumbCache = new Dictionary<string, Texture2D>();
        private Vector2 _scroll;
        private string[] _historyDirs = new string[0];
        private int _historyIndex;
        // 截图异步落盘,刚载入会话的几秒内持续重绘,好让缩略图自己补上
        private double _repaintUntil;

        [MenuItem("Tools/Render Stats Scanner")]
        private static void Open()
        {
            var win = GetWindow<RenderStatsScannerWindow>("Render Stats Scanner");
            win.minSize = new Vector2(680, 600);
        }

        private void OnEnable()
        {
            LoadPrefs();
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            RefreshHistory();
            // domain reload 后:把退出 Play 前那一轮的结果接回来
            var pending = SessionState.GetString(SS_SESSION_DIR, string.Empty);
            if (!string.IsNullOrEmpty(pending) && Directory.Exists(pending)) LoadSession(pending);
            else if (_historyDirs.Length > 0) LoadSession(_historyDirs[0]);
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            CloseWriter();
            ClearThumbCache();
            SavePrefs();
        }

        // ── 采样主循环 ────────────────────────────────────────────────

        private void OnEditorUpdate()
        {
            TickPendingHtml();
            if (EditorApplication.timeSinceStartup < _repaintUntil) Repaint();

            if (!_isSampling || !EditorApplication.isPlaying) return;

            // 仅在新渲染帧采样,避免同一帧被 update 多次重复统计
            var renderFrame = Time.renderedFrameCount;
            if (renderFrame == _lastRenderedFrame) return;
            _lastRenderedFrame = renderFrame;

            _cur = Frame.Capture();
            _sampledFrameCount++;
            _sumBatches += _cur.Batches;
            _sumSetPass += _cur.SetPass;
            _sumDrawCalls += _cur.DrawCalls;
            _sumTriangles += _cur.Triangles;
            _sumVertices += _cur.Vertices;
            if (_cur.Batches > _maxBatches) _maxBatches = _cur.Batches;
            if (_cur.SetPass > _maxSetPass) _maxSetPass = _cur.SetPass;
            if (_cur.DrawCalls > _maxDrawCalls) _maxDrawCalls = _cur.DrawCalls;
            if (_cur.Triangles > _maxTriangles) _maxTriangles = _cur.Triangles;
            if (_cur.Vertices > _maxVertices) _maxVertices = _cur.Vertices;

            CheckAnomalies();
            PushBaseline(_cur.Batches);

            if (_sampledFrameCount % 10 == 0) Repaint();
        }

        /// <summary>基线只收正常帧之外的全部帧;中位数天然抗少量尖峰,不必额外剔除。</summary>
        private void PushBaseline(int batches)
        {
            _baselineBuf[_baselineHead] = batches;
            _baselineHead = (_baselineHead + 1) % BASELINE_WINDOW;
            if (_baselineCount < BASELINE_WINDOW) _baselineCount++;
        }

        private int BaselineMedian()
        {
            if (_baselineCount == 0) return 0;
            var tmp = new int[_baselineCount];
            Array.Copy(_baselineBuf, tmp, _baselineCount);
            Array.Sort(tmp);
            return tmp[_baselineCount / 2];
        }

        private void CheckAnomalies()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastSnapshotTime < _snapshotCooldownSec) return;

            string reason = null;
            int reasonValue = 0;
            int baseline = 0;

            // 相对突增优先:它才是"异常增高",绝对阈值只是兜底
            if (_spikeEnabled && _baselineCount >= BASELINE_WINDOW)
            {
                var med = BaselineMedian();
                if (med > 0 && _cur.Batches >= med * _spikeRatio && _cur.Batches - med >= _spikeMinDelta)
                {
                    reason = "BatchesSpike";
                    reasonValue = _cur.Batches;
                    baseline = med;
                }
            }

            if (reason == null)
            {
                if (_cur.Batches > _batchesThreshold) { reason = "Batches"; reasonValue = _cur.Batches; }
                else if (_cur.SetPass > _setPassThreshold) { reason = "SetPass"; reasonValue = _cur.SetPass; }
                else if (_cur.DrawCalls > _drawCallsThreshold) { reason = "DrawCalls"; reasonValue = _cur.DrawCalls; }
                else if (_cur.Triangles > _trianglesThreshold) { reason = "Triangles"; reasonValue = _cur.Triangles; }
                else if (_cur.Vertices > _verticesThreshold) { reason = "Vertices"; reasonValue = _cur.Vertices; }
            }

            if (reason == null) return;

            var relShot = CaptureSnapshot();
            var evt = new AnomalyEvent
            {
                Index = _events.Count + 1,
                FrameIndex = _sampledFrameCount,
                TimeSec = now - _sampleStartTime,
                Reason = reason,
                TriggerValue = reasonValue,
                Baseline = baseline,
                Scene = SceneManager.GetActiveScene().name,
                Stats = _cur,
                ShotRelPath = relShot
            };
            _events.Add(evt);
            WriteEventRow(evt);
            _lastSnapshotTime = now;
            Repaint();
        }

        private string CaptureSnapshot()
        {
            var shotDir = Path.Combine(_sessionDir, SHOTS_SUBDIR);
            Directory.CreateDirectory(shotDir);
            _shotIndex++;
            var rel = string.Format(CultureInfo.InvariantCulture, "{0}/shot_{1:D4}.png", SHOTS_SUBDIR, _shotIndex);
            // Play 模式下截 Game View;异步写盘,下一渲染帧后文件才可读,所以报告生成要延迟
            ScreenCapture.CaptureScreenshot(Path.GetFullPath(Path.Combine(_sessionDir, rel)));
            return rel;
        }

        // ── 会话生命周期 ──────────────────────────────────────────────

        private void StartScan()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("Not in Play Mode",
                    "请先点击 Unity Editor 顶部的 Play 按钮进入游戏,开始游玩后再点击 Start Scan。", "OK");
                return;
            }

            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            _sessionDir = Path.Combine(ROOT_DIR, "session_" + ts);
            Directory.CreateDirectory(_sessionDir);
            SessionState.SetString(SS_SESSION_DIR, _sessionDir);
            SessionState.SetFloat(SS_PENDING_HTML_AT, 0f);

            _isSampling = true;
            _sampleStartTime = EditorApplication.timeSinceStartup;
            _sampledFrameCount = 0;
            _lastRenderedFrame = -1;
            _shotIndex = 0;
            _lastSnapshotTime = -1.0;
            _baselineCount = _baselineHead = 0;
            _sumBatches = _sumSetPass = _sumDrawCalls = _sumTriangles = _sumVertices = 0;
            _maxBatches = _maxSetPass = _maxDrawCalls = _maxTriangles = _maxVertices = 0;
            _events.Clear();
            _summaryLines.Clear();
            _loadedSessionDir = _sessionDir;
            ClearThumbCache();
            SavePrefs();

            // 事件边采边落盘:退出 Play 触发 domain reload 时内存字段会清空,磁盘才是真相
            _eventWriter = new StreamWriter(Path.Combine(_sessionDir, "events.tsv"), false, Encoding.UTF8);
            _eventWriter.WriteLine(string.Join("\t", AnomalyEvent.Header));
            _eventWriter.Flush();

            Debug.Log("[RenderStatsScanner] 采样开始 → " + _sessionDir);
        }

        /// <summary>结束采样:写概览、关闭流、排队生成 HTML(等截图落盘)。停止扫描与退出 Play 都走这里。</summary>
        private void StopScan()
        {
            if (!_isSampling) return;
            _isSampling = false;

            WriteSummary();
            CloseWriter();

            SessionState.SetString(SS_SESSION_DIR, _sessionDir);
            SessionState.SetFloat(SS_PENDING_HTML_AT, (float)(EditorApplication.timeSinceStartup + HTML_DELAY_SEC));

            Debug.Log(string.Format("[RenderStatsScanner] 采样结束:{0} 帧 / {1} 次异常 → {2}",
                _sampledFrameCount, _events.Count, _sessionDir));
            Repaint();
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            // ExitingPlayMode 时 GameView 尚在,写盘安全;之后的 domain reload 会清空内存字段
            if (state == PlayModeStateChange.ExitingPlayMode && _isSampling) StopScan();
        }

        /// <summary>截图是异步落盘的,到点后再生成 HTML 并把会话读回窗口。跨 domain reload 靠 SessionState 续命。</summary>
        private void TickPendingHtml()
        {
            var at = SessionState.GetFloat(SS_PENDING_HTML_AT, 0f);
            if (at <= 0f) return;
            // reload 后 timeSinceStartup 不重置(编辑器进程未退出),直接比较即可
            if (EditorApplication.timeSinceStartup < at) return;
            SessionState.SetFloat(SS_PENDING_HTML_AT, 0f);

            var dir = SessionState.GetString(SS_SESSION_DIR, string.Empty);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            var html = BuildReport(dir);
            RefreshHistory();
            LoadSession(dir);
            if (!string.IsNullOrEmpty(html)) Debug.Log("[RenderStatsScanner] 报告已生成:" + Path.GetFullPath(html));
            Repaint();
        }

        private void CloseWriter()
        {
            if (_eventWriter == null) return;
            try { _eventWriter.Flush(); _eventWriter.Dispose(); }
            catch (Exception e) { Debug.LogWarning("[RenderStatsScanner] 关闭事件流失败:" + e.Message); }
            _eventWriter = null;
        }

        private void WriteEventRow(AnomalyEvent e)
        {
            if (_eventWriter == null) return;
            try { _eventWriter.WriteLine(e.ToRow()); _eventWriter.Flush(); }
            catch (Exception ex) { Debug.LogWarning("[RenderStatsScanner] 写事件失败:" + ex.Message); }
        }

        private void WriteSummary()
        {
            var duration = EditorApplication.timeSinceStartup - _sampleStartTime;
            var sb = new StringBuilder();
            sb.AppendLine("key\tvalue");
            sb.AppendLine("session\t" + Path.GetFileName(_sessionDir));
            sb.AppendLine("started\t" + DateTime.Now.AddSeconds(-duration).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine(Row("duration_sec", duration.ToString("F1", CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("frames", _sampledFrameCount.ToString()));
            sb.AppendLine(Row("anomalies", _events.Count.ToString()));
            AppendStat(sb, "batches", _sumBatches, _maxBatches, _batchesThreshold);
            AppendStat(sb, "setpass", _sumSetPass, _maxSetPass, _setPassThreshold);
            AppendStat(sb, "drawcalls", _sumDrawCalls, _maxDrawCalls, _drawCallsThreshold);
            AppendStat(sb, "triangles", _sumTriangles, _maxTriangles, _trianglesThreshold);
            AppendStat(sb, "vertices", _sumVertices, _maxVertices, _verticesThreshold);
            sb.AppendLine(Row("spike_enabled", _spikeEnabled ? "1" : "0"));
            sb.AppendLine(Row("spike_ratio", _spikeRatio.ToString("F2", CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("spike_min_delta", _spikeMinDelta.ToString()));
            File.WriteAllText(Path.Combine(_sessionDir, "summary.tsv"), sb.ToString(), Encoding.UTF8);
        }

        private void AppendStat(StringBuilder sb, string name, long sum, int max, int threshold)
        {
            var avg = _sampledFrameCount > 0 ? (double)sum / _sampledFrameCount : 0;
            sb.AppendLine(Row(name + "_avg", avg.ToString("F1", CultureInfo.InvariantCulture)));
            sb.AppendLine(Row(name + "_max", max.ToString()));
            sb.AppendLine(Row(name + "_threshold", threshold.ToString()));
        }

        private static string Row(string k, string v)
        {
            return k + "\t" + v;
        }

        // ── 读回磁盘会话 ──────────────────────────────────────────────

        private void RefreshHistory()
        {
            if (!Directory.Exists(ROOT_DIR)) { _historyDirs = new string[0]; return; }
            _historyDirs = Directory.GetDirectories(ROOT_DIR, "session_*")
                .OrderByDescending(d => d)
                .ToArray();
        }

        private void LoadSession(string dir)
        {
            if (_isSampling && dir != _sessionDir) return; // 采样中不切换视图
            _loadedSessionDir = dir;
            _historyIndex = Math.Max(0, Array.IndexOf(_historyDirs, dir));
            _repaintUntil = EditorApplication.timeSinceStartup + 5.0;
            ClearThumbCache();

            _summaryLines.Clear();
            var sumPath = Path.Combine(dir, "summary.tsv");
            if (File.Exists(sumPath))
            {
                foreach (var line in File.ReadAllLines(sumPath).Skip(1))
                    if (!string.IsNullOrEmpty(line)) _summaryLines.Add(line);
            }

            // 采样中内存里的事件就是最新的,不必回读
            if (_isSampling) return;
            _events.Clear();
            var evtPath = Path.Combine(dir, "events.tsv");
            if (!File.Exists(evtPath)) return;
            foreach (var line in File.ReadAllLines(evtPath).Skip(1))
            {
                var e = AnomalyEvent.FromRow(line);
                if (e != null) _events.Add(e);
            }
        }

        private string SummaryValue(string key)
        {
            foreach (var l in _summaryLines)
            {
                var i = l.IndexOf('\t');
                if (i > 0 && l.Substring(0, i) == key) return l.Substring(i + 1);
            }
            return "-";
        }

        // ── HTML 报告 ────────────────────────────────────────────────

        private const string REPORT_CSS =
            "body{margin:0;padding:32px;background:#16181d;color:#e6e8ec;" +
            "font:14px/1.6 -apple-system,'PingFang SC',Segoe UI,sans-serif}" +
            "h1{font-size:20px;margin:0 0 4px}.sub{color:#9aa0ab;font-size:13px;margin-bottom:24px}" +
            "table{border-collapse:collapse;margin-bottom:24px}" +
            "td,th{border:1px solid #2c3038;padding:6px 12px;text-align:left;font-size:13px}" +
            "th{background:#20242b;color:#9aa0ab;font-weight:600}" +
            ".ev{display:flex;gap:20px;background:#1c1f26;border:1px solid #2c3038;border-radius:8px;" +
            "padding:16px;margin-bottom:16px;align-items:flex-start;flex-wrap:wrap}" +
            ".ev img{width:420px;max-width:100%;border-radius:4px;border:1px solid #2c3038;background:#000}" +
            ".ev .meta{flex:1;min-width:320px}" +
            ".tag{display:inline-block;background:#3a2226;color:#ff8a95;border-radius:4px;" +
            "padding:2px 8px;font-size:12px;margin-left:8px}" +
            ".miss{width:420px;height:200px;display:flex;align-items:center;justify-content:center;" +
            "background:#0f1114;border:1px dashed #2c3038;border-radius:4px;color:#6b7280}";

        /// <summary>从磁盘产物生成自包含报告(截图走相对路径,整个 session 目录可直接打包发走)。</summary>
        private string BuildReport(string dir)
        {
            try
            {
                var evtPath = Path.Combine(dir, "events.tsv");
                var events = new List<AnomalyEvent>();
                if (File.Exists(evtPath))
                {
                    foreach (var line in File.ReadAllLines(evtPath).Skip(1))
                    {
                        var e = AnomalyEvent.FromRow(line);
                        if (e != null) events.Add(e);
                    }
                }

                var summary = new List<KeyValuePair<string, string>>();
                var sumPath = Path.Combine(dir, "summary.tsv");
                if (File.Exists(sumPath))
                {
                    foreach (var line in File.ReadAllLines(sumPath).Skip(1))
                    {
                        var i = line.IndexOf('\t');
                        if (i > 0) summary.Add(new KeyValuePair<string, string>(line.Substring(0, i), line.Substring(i + 1)));
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
                sb.AppendLine("<title>Render Stats Scanner " + Esc(Path.GetFileName(dir)) + "</title>");
                sb.AppendLine("<style>" + REPORT_CSS + "</style></head><body>");
                sb.AppendLine("<h1>渲染统计异常报告</h1>");
                sb.AppendLine("<div class=\"sub\">" + Esc(Path.GetFileName(dir)) + " · 共 " + events.Count + " 次异常</div>");

                sb.AppendLine("<h3>采样概览</h3><table><tr><th>指标</th><th>值</th></tr>");
                foreach (var kv in summary)
                    sb.AppendLine("<tr><td>" + Esc(kv.Key) + "</td><td>" + Esc(kv.Value) + "</td></tr>");
                sb.AppendLine("</table>");

                sb.AppendLine("<h3>异常事件</h3>");
                if (events.Count == 0)
                {
                    sb.AppendLine("<p>本次采样未触发任何阈值。</p>");
                }
                foreach (var e in events)
                {
                    sb.AppendLine("<div class=\"ev\">");
                    var shotAbs = Path.Combine(dir, e.ShotRelPath ?? string.Empty);
                    if (!string.IsNullOrEmpty(e.ShotRelPath) && File.Exists(shotAbs))
                        sb.AppendLine("<a href=\"" + Esc(e.ShotRelPath) + "\"><img src=\"" + Esc(e.ShotRelPath) + "\"></a>");
                    else
                        sb.AppendLine("<div class=\"miss\">截图缺失</div>");

                    sb.AppendLine("<div class=\"meta\">");
                    sb.AppendLine("<b>#" + e.Index + " · T+" + e.TimeSec.ToString("F2", CultureInfo.InvariantCulture)
                        + "s · frame " + e.FrameIndex + "</b><span class=\"tag\">" + Esc(e.Reason) + " = " + e.TriggerValue
                        + (e.Baseline > 0 ? " (基线 " + e.Baseline + ")" : "") + "</span>");
                    sb.AppendLine("<table>");
                    AppendRow(sb, "场景", e.Scene);
                    AppendRow(sb, "Batches", e.Stats.Batches.ToString());
                    AppendRow(sb, "SetPass Calls", e.Stats.SetPass.ToString());
                    AppendRow(sb, "Draw Calls", e.Stats.DrawCalls.ToString());
                    AppendRow(sb, "└ 动态合批 / 静态合批 / GPU Instancing",
                        e.Stats.DynamicBatched + " / " + e.Stats.StaticBatched + " / " + e.Stats.InstancedBatched);
                    AppendRow(sb, "Triangles", e.Stats.Triangles.ToString("N0", CultureInfo.InvariantCulture));
                    AppendRow(sb, "Vertices", e.Stats.Vertices.ToString("N0", CultureInfo.InvariantCulture));
                    AppendRow(sb, "Shadow Casters", e.Stats.ShadowCasters.ToString());
                    AppendRow(sb, "RenderTexture 切换", e.Stats.RenderTextureChanges.ToString());
                    AppendRow(sb, "纹理数 / 显存", e.Stats.UsedTextureCount + " / "
                        + (e.Stats.UsedTextureMemory / 1024f / 1024f).ToString("F1", CultureInfo.InvariantCulture) + " MB");
                    AppendRow(sb, "可见蒙皮网格 / 播放中动画", e.Stats.VisibleSkinnedMeshes + " / " + e.Stats.AnimationsPlaying);
                    AppendRow(sb, "帧耗时 / 渲染耗时",
                        e.Stats.FrameTime.ToString("F2", CultureInfo.InvariantCulture) + " ms / "
                        + e.Stats.RenderTime.ToString("F2", CultureInfo.InvariantCulture) + " ms");
                    AppendRow(sb, "分辨率", e.Stats.ScreenRes);
                    sb.AppendLine("</table></div></div>");
                }

                sb.AppendLine("</body></html>");
                var outPath = Path.Combine(dir, "report.html");
                File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
                return outPath;
            }
            catch (Exception e)
            {
                Debug.LogError("[RenderStatsScanner] 生成报告失败:" + e);
                return null;
            }
        }

        private static void AppendRow(StringBuilder sb, string k, string v)
        {
            sb.AppendLine("<tr><td>" + Esc(k) + "</td><td>" + Esc(v) + "</td></tr>");
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        // ── 窗口 UI ──────────────────────────────────────────────────

        private void OnGUI()
        {
            DrawHeader();
            DrawConfig();
            DrawControl();
            DrawResult();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(4);
            if (_isSampling)
            {
                EditorGUILayout.HelpBox(string.Format("采样中 · {0} 帧 · {1} 次异常 · 基线 batches {2}",
                    _sampledFrameCount, _events.Count, BaselineMedian()), MessageType.Info);
                EditorGUILayout.LabelField(string.Format(
                    "实时  Batches {0} · SetPass {1} · Draw {2} · Tris {3} · Verts {4}",
                    _cur.Batches, _cur.SetPass, _cur.DrawCalls, _cur.Triangles, _cur.Vertices),
                    EditorStyles.boldLabel);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "1) 点 Play 进游戏 → 2) 点 Start Scan → 3) 正常游玩,异常时自动截图 → " +
                    "4) 退出 Play(或点 Stop Scan)后自动生成报告", MessageType.None);
            }
            EditorGUILayout.Space(4);
        }

        private void DrawConfig()
        {
            EditorGUILayout.LabelField("异常判定", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(_isSampling);
            EditorGUI.BeginChangeCheck();

            _spikeEnabled = EditorGUILayout.ToggleLeft(
                "相对突增检测(batches 高过近期基线)", _spikeEnabled);
            EditorGUI.indentLevel++;
            EditorGUI.BeginDisabledGroup(!_spikeEnabled);
            _spikeRatio = EditorGUILayout.Slider("倍率 ≥", _spikeRatio, 1.1f, 4f);
            _spikeMinDelta = EditorGUILayout.IntField("且绝对增量 ≥", _spikeMinDelta);
            EditorGUI.EndDisabledGroup();
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField("绝对阈值(兜底)", EditorStyles.miniBoldLabel);
            _batchesThreshold = EditorGUILayout.IntField("Batches >", _batchesThreshold);
            _setPassThreshold = EditorGUILayout.IntField("SetPass Calls >", _setPassThreshold);
            _drawCallsThreshold = EditorGUILayout.IntField("Draw Calls >", _drawCallsThreshold);
            _trianglesThreshold = EditorGUILayout.IntField("Triangles >", _trianglesThreshold);
            _verticesThreshold = EditorGUILayout.IntField("Vertices >", _verticesThreshold);
            _snapshotCooldownSec = EditorGUILayout.FloatField("截图冷却 (s)", _snapshotCooldownSec);

            if (EditorGUI.EndChangeCheck()) SavePrefs();
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.Space(6);
        }

        private void DrawControl()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginDisabledGroup(_isSampling || !EditorApplication.isPlaying);
                if (GUILayout.Button("Start Scan", GUILayout.Height(28))) StartScan();
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(!_isSampling);
                if (GUILayout.Button("Stop Scan", GUILayout.Height(28))) StopScan();
                EditorGUI.EndDisabledGroup();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginDisabledGroup(_isSampling || _historyDirs.Length == 0);
                var names = _historyDirs.Select(Path.GetFileName).ToArray();
                var idx = EditorGUILayout.Popup("历史报告", Mathf.Clamp(_historyIndex, 0, Math.Max(0, names.Length - 1)), names);
                if (names.Length > 0 && idx != _historyIndex) LoadSession(_historyDirs[idx]);

                if (GUILayout.Button("刷新", GUILayout.Width(60))) { RefreshHistory(); }
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(_loadedSessionDir));
                if (GUILayout.Button("打开报告", GUILayout.Width(80))) OpenReport();
                if (GUILayout.Button("打开目录", GUILayout.Width(80)))
                    EditorUtility.RevealInFinder(Path.GetFullPath(_loadedSessionDir) + Path.DirectorySeparatorChar);
                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.Space(8);
        }

        private void OpenReport()
        {
            var html = Path.Combine(_loadedSessionDir, "report.html");
            if (!File.Exists(html)) html = BuildReport(_loadedSessionDir); // 老会话缺报告时补生成
            if (!string.IsNullOrEmpty(html) && File.Exists(html)) Application.OpenURL("file://" + Path.GetFullPath(html));
        }

        private void DrawResult()
        {
            if (_summaryLines.Count == 0 && _events.Count == 0 && !_isSampling)
            {
                EditorGUILayout.HelpBox("还没有采样记录。进入 Play 模式后点 Start Scan。", MessageType.None);
                return;
            }

            if (!_isSampling && !string.IsNullOrEmpty(_loadedSessionDir))
                EditorGUILayout.LabelField("会话 " + Path.GetFileName(_loadedSessionDir), EditorStyles.boldLabel);

            if (_isSampling)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawStatBox("Batches", _sumBatches, _maxBatches);
                    DrawStatBox("SetPass", _sumSetPass, _maxSetPass);
                    DrawStatBox("DrawCalls", _sumDrawCalls, _maxDrawCalls);
                }
            }
            else if (_summaryLines.Count > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawSummaryBox("Batches", "batches");
                    DrawSummaryBox("SetPass", "setpass");
                    DrawSummaryBox("DrawCalls", "drawcalls");
                }
                EditorGUILayout.LabelField(string.Format("帧数 {0} · 时长 {1}s · 异常 {2}",
                    SummaryValue("frames"), SummaryValue("duration_sec"), SummaryValue("anomalies")));
            }
            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField("异常事件 (" + _events.Count + ")", EditorStyles.boldLabel);
            if (_events.Count == 0)
            {
                EditorGUILayout.HelpBox("未触发任何判定。可调低阈值/倍率,或延长游玩时间。", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var e in _events)
            {
                using (new EditorGUILayout.HorizontalScope(GUI.skin.box))
                {
                    var abs = string.IsNullOrEmpty(e.ShotRelPath) || string.IsNullOrEmpty(_loadedSessionDir)
                        ? null : Path.Combine(_loadedSessionDir, e.ShotRelPath);
                    var tex = LoadThumb(abs);
                    if (tex != null)
                    {
                        const float w = 150f;
                        GUILayout.Box(tex, GUILayout.Width(w), GUILayout.Height(w * tex.height / tex.width));
                    }
                    else
                    {
                        GUILayout.Box("(截图生成中…)", GUILayout.Width(150), GUILayout.Height(90));
                    }

                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField(string.Format("#{0}  frame {1}  T+{2:F2}s  [{3}]",
                            e.Index, e.FrameIndex, e.TimeSec, e.Scene), EditorStyles.boldLabel);
                        EditorGUILayout.LabelField(e.Baseline > 0
                            ? string.Format("触发:{0} = {1}(基线 {2})", e.Reason, e.TriggerValue, e.Baseline)
                            : string.Format("触发:{0} = {1}", e.Reason, e.TriggerValue));
                        EditorGUILayout.LabelField(string.Format("Batches {0} · SetPass {1} · Draw {2}(动 {3}/静 {4}/Inst {5})",
                            e.Stats.Batches, e.Stats.SetPass, e.Stats.DrawCalls,
                            e.Stats.DynamicBatched, e.Stats.StaticBatched, e.Stats.InstancedBatched));
                        EditorGUILayout.LabelField(string.Format("Tris {0:N0} · Verts {1:N0} · 帧 {2:F1}ms / 渲染 {3:F1}ms",
                            e.Stats.Triangles, e.Stats.Vertices, e.Stats.FrameTime, e.Stats.RenderTime));
                        if (abs != null && GUILayout.Button("定位截图", GUILayout.Width(100)))
                            EditorUtility.RevealInFinder(Path.GetFullPath(abs));
                    }
                }
                EditorGUILayout.Space(2);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawStatBox(string name, long sum, int max)
        {
            var avg = _sampledFrameCount > 0 ? (double)sum / _sampledFrameCount : 0;
            using (new EditorGUILayout.VerticalScope(GUI.skin.box, GUILayout.Width(200)))
            {
                EditorGUILayout.LabelField(name, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("avg " + avg.ToString("F1", CultureInfo.InvariantCulture));
                EditorGUILayout.LabelField("max " + max);
            }
        }

        private void DrawSummaryBox(string title, string key)
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box, GUILayout.Width(200)))
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("avg " + SummaryValue(key + "_avg"));
                EditorGUILayout.LabelField("max " + SummaryValue(key + "_max"));
            }
        }

        private Texture2D LoadThumb(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Texture2D tex;
            if (_thumbCache.TryGetValue(path, out tex)) return tex;
            if (!File.Exists(path)) return null; // 尚未落盘,下次重绘再试
            try
            {
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!t.LoadImage(File.ReadAllBytes(path))) { DestroyImmediate(t); return null; }
                _thumbCache[path] = t;
                return t;
            }
            catch { return null; }
        }

        private void ClearThumbCache()
        {
            foreach (var kv in _thumbCache)
                if (kv.Value != null) DestroyImmediate(kv.Value);
            _thumbCache.Clear();
        }

        // ── 配置持久化 ───────────────────────────────────────────────

        private void LoadPrefs()
        {
            _batchesThreshold = EditorPrefs.GetInt(PREF_PREFIX + "batches", DEFAULT_BATCHES_THRESHOLD);
            _setPassThreshold = EditorPrefs.GetInt(PREF_PREFIX + "setpass", DEFAULT_SETPASS_THRESHOLD);
            _drawCallsThreshold = EditorPrefs.GetInt(PREF_PREFIX + "drawcalls", DEFAULT_DRAWCALLS_THRESHOLD);
            _trianglesThreshold = EditorPrefs.GetInt(PREF_PREFIX + "tris", DEFAULT_TRIANGLES_THRESHOLD);
            _verticesThreshold = EditorPrefs.GetInt(PREF_PREFIX + "verts", DEFAULT_VERTICES_THRESHOLD);
            _snapshotCooldownSec = EditorPrefs.GetFloat(PREF_PREFIX + "cooldown", DEFAULT_SNAPSHOT_COOLDOWN);
            _spikeEnabled = EditorPrefs.GetBool(PREF_PREFIX + "spike", true);
            _spikeRatio = EditorPrefs.GetFloat(PREF_PREFIX + "spikeRatio", DEFAULT_SPIKE_RATIO);
            _spikeMinDelta = EditorPrefs.GetInt(PREF_PREFIX + "spikeDelta", DEFAULT_SPIKE_MIN_DELTA);
        }

        private void SavePrefs()
        {
            EditorPrefs.SetInt(PREF_PREFIX + "batches", _batchesThreshold);
            EditorPrefs.SetInt(PREF_PREFIX + "setpass", _setPassThreshold);
            EditorPrefs.SetInt(PREF_PREFIX + "drawcalls", _drawCallsThreshold);
            EditorPrefs.SetInt(PREF_PREFIX + "tris", _trianglesThreshold);
            EditorPrefs.SetInt(PREF_PREFIX + "verts", _verticesThreshold);
            EditorPrefs.SetFloat(PREF_PREFIX + "cooldown", _snapshotCooldownSec);
            EditorPrefs.SetBool(PREF_PREFIX + "spike", _spikeEnabled);
            EditorPrefs.SetFloat(PREF_PREFIX + "spikeRatio", _spikeRatio);
            EditorPrefs.SetInt(PREF_PREFIX + "spikeDelta", _spikeMinDelta);
        }

        // ── 数据结构 ────────────────────────────────────────────────

        /// <summary>一帧的 Stats 面板快照(UnityStats 与 Stats 窗口同源)。</summary>
        private struct Frame
        {
            public int Batches, SetPass, DrawCalls;
            public int DynamicBatched, StaticBatched, InstancedBatched;
            public int Triangles, Vertices, ShadowCasters, RenderTextureChanges;
            public int UsedTextureCount;
            public long UsedTextureMemory;
            public int VisibleSkinnedMeshes, AnimationsPlaying;
            public float FrameTime, RenderTime;
            public string ScreenRes;

            public static Frame Capture()
            {
                return new Frame
                {
                    Batches = UnityStats.batches,
                    SetPass = UnityStats.setPassCalls,
                    DrawCalls = UnityStats.drawCalls,
                    DynamicBatched = UnityStats.dynamicBatchedDrawCalls,
                    StaticBatched = UnityStats.staticBatchedDrawCalls,
                    InstancedBatched = UnityStats.instancedBatchedDrawCalls,
                    Triangles = UnityStats.triangles,
                    Vertices = UnityStats.vertices,
                    ShadowCasters = UnityStats.shadowCasters,
                    RenderTextureChanges = UnityStats.renderTextureChanges,
                    UsedTextureCount = UnityStats.usedTextureCount,
                    // 该字段在不同版本分别是 int / long,转一道避免版本耦合
                    UsedTextureMemory = Convert.ToInt64(UnityStats.usedTextureMemorySize),
                    VisibleSkinnedMeshes = UnityStats.visibleSkinnedMeshes,
                    AnimationsPlaying = UnityStats.animationComponentsPlaying,
                    FrameTime = UnityStats.frameTime * 1000f,
                    RenderTime = UnityStats.renderTime * 1000f,
                    ScreenRes = UnityStats.screenRes
                };
            }
        }

        private class AnomalyEvent
        {
            public int Index;
            public int FrameIndex;
            public double TimeSec;
            public string Reason;
            public int TriggerValue;
            public int Baseline;
            public string Scene;
            public Frame Stats;
            public string ShotRelPath;

            public static readonly string[] Header =
            {
                "idx", "frame", "time_sec", "reason", "trigger_value", "baseline", "scene",
                "batches", "setpass", "drawcalls", "dyn_batched", "static_batched", "inst_batched",
                "triangles", "vertices", "shadow_casters", "rt_changes", "texture_count", "texture_mem_bytes",
                "skinned_meshes", "animations", "frame_ms", "render_ms", "screen_res", "shot"
            };

            public string ToRow()
            {
                var c = CultureInfo.InvariantCulture;
                return string.Join("\t", new[]
                {
                    Index.ToString(c), FrameIndex.ToString(c), TimeSec.ToString("F3", c),
                    Clean(Reason), TriggerValue.ToString(c), Baseline.ToString(c), Clean(Scene),
                    Stats.Batches.ToString(c), Stats.SetPass.ToString(c), Stats.DrawCalls.ToString(c),
                    Stats.DynamicBatched.ToString(c), Stats.StaticBatched.ToString(c), Stats.InstancedBatched.ToString(c),
                    Stats.Triangles.ToString(c), Stats.Vertices.ToString(c), Stats.ShadowCasters.ToString(c),
                    Stats.RenderTextureChanges.ToString(c), Stats.UsedTextureCount.ToString(c),
                    Stats.UsedTextureMemory.ToString(c), Stats.VisibleSkinnedMeshes.ToString(c),
                    Stats.AnimationsPlaying.ToString(c), Stats.FrameTime.ToString("F3", c),
                    Stats.RenderTime.ToString("F3", c), Clean(Stats.ScreenRes), Clean(ShotRelPath)
                });
            }

            public static AnomalyEvent FromRow(string line)
            {
                if (string.IsNullOrEmpty(line)) return null;
                var f = line.Split('\t');
                if (f.Length < Header.Length) return null;
                var c = CultureInfo.InvariantCulture;
                try
                {
                    return new AnomalyEvent
                    {
                        Index = int.Parse(f[0], c),
                        FrameIndex = int.Parse(f[1], c),
                        TimeSec = double.Parse(f[2], c),
                        Reason = f[3],
                        TriggerValue = int.Parse(f[4], c),
                        Baseline = int.Parse(f[5], c),
                        Scene = f[6],
                        Stats = new Frame
                        {
                            Batches = int.Parse(f[7], c),
                            SetPass = int.Parse(f[8], c),
                            DrawCalls = int.Parse(f[9], c),
                            DynamicBatched = int.Parse(f[10], c),
                            StaticBatched = int.Parse(f[11], c),
                            InstancedBatched = int.Parse(f[12], c),
                            Triangles = int.Parse(f[13], c),
                            Vertices = int.Parse(f[14], c),
                            ShadowCasters = int.Parse(f[15], c),
                            RenderTextureChanges = int.Parse(f[16], c),
                            UsedTextureCount = int.Parse(f[17], c),
                            UsedTextureMemory = long.Parse(f[18], c),
                            VisibleSkinnedMeshes = int.Parse(f[19], c),
                            AnimationsPlaying = int.Parse(f[20], c),
                            FrameTime = float.Parse(f[21], c),
                            RenderTime = float.Parse(f[22], c),
                            ScreenRes = f[23]
                        },
                        ShotRelPath = f[24]
                    };
                }
                catch
                {
                    return null; // 行损坏(例如 Editor 崩溃时写了半行)就跳过,不拖垮整份报告
                }
            }

            private static string Clean(string s)
            {
                return string.IsNullOrEmpty(s) ? string.Empty : s.Replace('\t', ' ').Replace('\n', ' ');
            }
        }
    }
}
