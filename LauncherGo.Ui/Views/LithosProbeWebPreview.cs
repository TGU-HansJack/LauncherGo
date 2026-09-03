using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Platform;
using LauncherGo.Domains.Models;

namespace LauncherGo.Ui.Views;

internal static class LithosProbeWebPreview
{
    public static string OpenInBrowser(LithosProbeReport report, bool isChinese)
    {
        var previewDirectory = Path.Combine(
            Path.GetTempPath(),
            "LauncherGo",
            "lithos-probe-preview",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(previewDirectory);

        var pagePath = Path.Combine(previewDirectory, "index.html");
        File.WriteAllText(pagePath, BuildChartHtml(report, isChinese), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo
        {
            FileName = pagePath,
            UseShellExecute = true
        });
        return pagePath;
    }

    internal static string BuildChartHtml(LithosProbeReport report, bool isChinese)
    {
        var payload = new BrowserPayload
        {
            IsChinese = isChinese,
            Schema = report.Schema,
            Kind = report.Kind,
            GeneratedAtUtc = report.GeneratedAtUtc,
            Server = report.Server,
            Census = report.Census,
            Windows = report.Windows,
            Mods = report.Mods,
            Profile = report.Profile,
            Tiers = report.SeriesTiers.Select(static tier => new TrendTierPayload
            {
                SpanSeconds = tier.SpanSeconds,
                Count = tier.Count,
                Fields = tier.Fields,
                Times = tier.Times.Select(static time => time / 1000d).ToList(),
                Values = tier.Values.ToDictionary(
                    static pair => pair.Key,
                    static pair => (IReadOnlyList<double?>)pair.Value
                        .Select(static value => double.IsFinite(value) ? value : (double?)null)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase)
            }).ToList()
        };
        var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
        });

        return $$"""
            <!doctype html>
            <html lang="{{(isChinese ? "zh-CN" : "en")}}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{(isChinese ? "Lithos Probe 交互图表" : "Lithos Probe interactive charts")}}</title>
            <style>{{ReadAssetText("Assets/ThirdParty/uPlot/uPlot.min.css")}}
            :root { color-scheme: light; --ink: #111827; --muted: #667085; --panel: #ffffff; --line: #e4e7ec; --grid: #eaecf0; --accent: #111827; --canvas: #f9fafb; --soft: #f2f4f7; }
            * { box-sizing: border-box; }
            html, body { margin: 0; min-height: 100%; background: var(--canvas); color: var(--ink); font-family: "Segoe UI", system-ui, sans-serif; }
            #app { max-width: 1440px; margin: 0 auto; padding: 28px 24px 56px; }
            header { display:flex; justify-content:space-between; align-items:flex-end; gap:16px; padding-bottom:20px; border-bottom:1px solid var(--line); }
            h1 { margin:0; font-size:26px; letter-spacing:-.02em; } h2 { margin:0 0 12px; font-size:16px; } h3 { margin:0; font-size:13px; }
            .meta { color:var(--muted); font-size:12px; line-height:1.7; text-align:right; }
            section.block { margin-top:24px; } .grid { display:grid; gap:10px; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); }
            .tile,.panel { border:1px solid var(--line); border-radius:4px; background:var(--panel); padding:12px; } .tile .label { color:var(--muted); font-size:11px; } .tile .value { margin-top:5px; font-size:18px; font-weight:600; }
            #toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 7px; padding-bottom: 9px; border-bottom: 1px solid var(--line); }
            #tiers { display: flex; flex-wrap: wrap; gap: 5px; margin-right: 8px; }
            button { min-height: 28px; border: 1px solid var(--line); border-radius: 3px; background: var(--panel); color: var(--ink); padding: 4px 8px; font: inherit; font-size: 12px; cursor: pointer; }
            button:hover { border-color: var(--accent); }
            button[aria-pressed="true"] { border-color: var(--accent); color: var(--accent); background: color-mix(in srgb, var(--accent) 12%, var(--panel)); }
            #readout { min-height: 25px; margin: 8px 0; color: var(--muted); font-size: 12px; }
            #charts { display: grid; grid-template-columns: repeat(auto-fit, minmax(360px, 1fr)); gap: 10px; }
            .chart { border: 1px solid var(--line); border-radius: 4px; background: var(--panel); padding: 10px; min-width: 0; }
            .chart:hover { border-color: var(--accent); }
            .chart-title { font-size: 13px; font-weight: 600; padding-bottom: 5px; }
            .plot-shell { position: relative; width: 100%; height: 238px; }
            .plot { width: 100%; height: 100%; }
            .chart-tip { position:absolute; z-index:2; pointer-events:none; display:none; max-width:calc(100% - 12px); padding:6px 8px; border:1px solid var(--line); border-radius:4px; background:rgba(255,255,255,.96); box-shadow:0 4px 14px rgba(16,24,40,.12); color:var(--ink); font-size:11px; line-height:1.45; white-space:nowrap; }
            .chart-subtitle { color:var(--muted); font-size:11px; padding:0 0 7px; }
            #table-shell { margin-top: 10px; border: 1px solid var(--line); border-radius: 4px; overflow: auto; max-height: 430px; }
            table { border-collapse: collapse; font-size: 12px; min-width: max-content; width: 100%; }
            th, td { padding: 5px 8px; text-align: right; border-bottom: 1px solid var(--grid); white-space: nowrap; }
            th { position: sticky; top: 0; background: var(--panel); color: var(--muted); font-weight: 600; }
            th:first-child, td:first-child { text-align: left; }
            #empty { padding: 32px; color: var(--muted); border: 1px dashed var(--line); border-radius: 4px; }
            .table-wrap { overflow:auto; border:1px solid var(--line); border-radius:4px; background:var(--panel); } .data-table { width:100%; border-collapse:collapse; font-size:12px; } .data-table th,.data-table td { padding:8px 10px; border-bottom:1px solid var(--grid); text-align:left; white-space:nowrap; } .data-table th { color:var(--muted); font-weight:600; background:var(--soft); } .muted { color:var(--muted); }
            details { border-top:1px solid var(--grid); padding:7px 0; } details:first-child { border-top:0; } summary { cursor:pointer; list-style:none; } summary::-webkit-details-marker { display:none; } .tree { margin-left:16px; } .tree-meta { color:var(--muted); font-size:11px; margin-left:8px; }
            .uplot { background: transparent; }
            </style>
            </head>
            <body>
            <main id="app">
              <header><div><h1>Lithos Probe</h1><div class="muted" id="report-kind"></div></div><div class="meta" id="report-meta"></div></header>
              <section class="block"><div class="grid" id="overview"></div></section>
              <section class="block"><h2 id="health-title"></h2><div class="table-wrap"><table class="data-table" id="health-table"></table></div></section>
              <section class="block"><h2 id="series-title"></h2>
              <div id="toolbar">
                <div id="tiers"></div>
                <button id="log" type="button" aria-pressed="false"></button>
                <button id="table" type="button" aria-pressed="false"></button>
              </div>
              <div id="readout"></div>
              <section id="charts"><div id="boot">Loading uPlot...</div></section>
              <section id="table-shell" hidden><table id="data-table"></table></section>
              </section>
              <section class="block"><h2 id="mods-title"></h2><div class="table-wrap"><table class="data-table" id="mods-table"></table></div></section>
              <section class="block"><h2 id="profile-title"></h2><div class="grid" id="profile-summary"></div><div class="grid"><div class="panel"><h3 id="mod-hotspots-title"></h3><div id="mod-hotspots"></div></div><div class="panel"><h3 id="module-hotspots-title"></h3><div id="module-hotspots"></div></div></div><div class="panel" style="margin-top:10px"><h3 id="threads-title"></h3><div id="threads"></div></div></section>
            </main>
            <script>{{ReadAssetText("Assets/ThirdParty/uPlot/uPlot.iife.min.js")}}</script>
            <script>
            document.body.dataset.lithosState = "starting";
            const reportFailure = error => {
              const detail = error && error.stack ? error.stack : String(error);
              document.body.dataset.lithosState = "error";
              document.body.dataset.lithosError = detail;
              const boot = document.getElementById("boot");
              if (boot) boot.textContent = "uPlot error: " + detail;
              if (typeof invokeCSharpAction === "function") invokeCSharpAction(JSON.stringify({ type: "error", detail }));
            };
            window.addEventListener("error", event => reportFailure(event.error || event.message));
            window.addEventListener("unhandledrejection", event => reportFailure(event.reason));
            try {
            (() => {
              "use strict";
              const payload = {{payloadJson}};
              const zh = payload.isChinese;
              const t = (cn, en) => zh ? cn : en;
              const $ = id => document.getElementById(id);
              const state = { tier: 0, log: false, table: false, plots: [] };
              const specs = [
                { title: "TPS", fields: ["tps"], names: ["TPS"], colors: ["#2677c9"] },
                { title: "MSPT (Mean / P95 / Max)", fields: ["msptMean", "msptP95", "msptMax"], names: ["Mean", "P95", "Max"], colors: ["#d26b2d", "#8759b0", "#bd3d43"] },
                { title: t("CPU 使用率", "CPU usage"), fields: ["cpuPercent"], names: ["CPU"], colors: ["#168c76"] },
                { title: t("工作集内存", "Working set memory"), fields: ["workingSetMb"], names: ["Working set"], colors: ["#a0419c"] },
                { title: t("托管堆", "Managed heap"), fields: ["managedHeapMb"], names: ["Managed heap"], colors: ["#5274c5"] },
                { title: t("分配速率", "Allocation rate"), fields: ["allocationMbPerSecond"], names: ["Allocation"], colors: ["#9d7825"] },
                { title: t("GC 暂停", "GC pause"), fields: ["gcPausePercent"], names: ["GC pause"], colors: ["#b45179"] },
                { title: t("GC 回收率", "GC collections"), fields: ["gen0PerSecond", "gen1PerSecond", "gen2PerSecond"], names: ["Gen0", "Gen1", "Gen2"], colors: ["#3983be", "#459771", "#c36c3d"] },
                { title: t("玩家数", "Players"), fields: ["players"], names: ["Players"], colors: ["#477db6"] },
                { title: t("已加载区块", "Loaded chunks"), fields: ["loadedChunks"], names: ["Loaded chunks"], colors: ["#728b40"] },
                { title: t("已加载实体", "Loaded entities"), fields: ["loadedEntities"], names: ["Loaded entities"], colors: ["#bb5d40"] },
                { title: t("网络吞吐", "Network throughput"), fields: ["networkKbPerSecond"], names: ["Network"], colors: ["#4e7fc2"] },
                { title: t("数据包速率", "Packet rate"), fields: ["packetsPerSecond"], names: ["Packets"], colors: ["#956b32"] }
              ];
              const allMetricFields = [...new Set((payload.tiers || []).flatMap(tier => [...(tier.fields || []), ...Object.keys(tier.values || {})]))];
              const knownMetricFields = new Set(specs.flatMap(spec => spec.fields));
              const extraMetricFields = allMetricFields.filter(field => !knownMetricFields.has(field));
              extraMetricFields.forEach((field, index) => specs.push({ title: field, fields: [field], names: [field], colors: ["#667085"] }));
              const metricFields = specs.flatMap(spec => spec.fields).filter((field, index, all) => all.indexOf(field) === index);
              const format = value => Number.isFinite(value) ? value.toLocaleString(zh ? "zh-CN" : "en-US", { maximumFractionDigits: 2 }) : "-";
              const timeText = seconds => new Date(seconds * 1000).toLocaleString(zh ? "zh-CN" : "en-US", { dateStyle: "short", timeStyle: "medium" });
              const axisTime = (seconds, span) => { const d = new Date(seconds * 1000); const pad = n => String(n).padStart(2, "0"); const clock = `${pad(d.getHours())}:${pad(d.getMinutes())}${span < 1800 ? ":" + pad(d.getSeconds()) : ""}`; return span >= 86400 ? `${pad(d.getMonth() + 1)}/${pad(d.getDate())} ${clock}` : clock; };
              const text = (cn, en) => zh ? cn : en;
              const esc = value => String(value ?? "-").replace(/[&<>\"]/g, ch => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[ch]));
              const setText = (id, value) => { const node = $(id); if (node) node.textContent = value; };
              const cell = value => `<td>${esc(value)}</td>`;
              const renderReport = () => {
                const s = payload.server || {}, c = payload.census || {};
                setText("report-kind", `${payload.kind || "-"} · schema ${payload.schema || "-"}`);
                $("report-meta").innerHTML = `${esc(text("生成时间", "Generated"))} ${esc(payload.generatedAtUtc ? new Date(payload.generatedAtUtc).toLocaleString(zh ? "zh-CN" : "en-US") : "-")}<br>${esc(s.gameVersion || "-")} · Probe ${esc(s.lithosVersion || "-")}`;
                const overview = [[text("玩家", "Players"),c.players],[text("已加载区块", "Loaded chunks"),c.loadedChunks],[text("已加载实体", "Loaded entities"),c.loadedEntities],[text("运行时间", "Uptime"),formatDuration(s.uptimeSeconds)],[text("Tick 时间", "Tick time"),format(s.tickTimeMs)+" ms"],[text("总 Tick", "Total ticks"),format(s.totalTicks)],[text("最大客户端", "Max clients"),s.maxClients],[text("处理器", "Processors"),s.processorCount],[text("运行时", "Runtime"),s.runtime || "-"],[text("操作系统", "Operating system"),s.operatingSystem || "-"],[text("架构", "Architecture"),s.architecture || "-"],[text("服务器 GC", "Server GC"),s.serverGc ? "ON" : "OFF"]];
                $("overview").innerHTML = overview.map(([label,value]) => `<div class="tile"><div class="label">${esc(label)}</div><div class="value">${esc(value)}</div></div>`).join("");
                setText("health-title", text("健康窗口", "Health windows"));
                const health = payload.windows || []; $("health-table").innerHTML = `<thead><tr>${[text("窗口","Window"),text("Tick","Ticks"),"TPS","Mean ms","Median ms","P95 ms","P99 ms","Max ms",text("覆盖","Coverage")].map(esc).map(x=>`<th>${x}</th>`).join("")}</tr></thead><tbody>${health.map(w=>`<tr>${cell(w.name)}${cell(format(w.ticks))}${cell(format(w.tps))}${cell(format(w.meanMs))}${cell(format(w.medianMs))}${cell(format(w.p95Ms))}${cell(format(w.p99Ms))}${cell(format(w.maxMs))}${cell(format(w.coveredSeconds)+" / "+format(w.seconds)+(w.seconds>0&&w.coveredSeconds<w.seconds*.9?" *":""))}</tr>`).join("")}</tbody>`;
                setText("series-title", text("时间序列", "Time series")); setText("mods-title", text("Mod 清单", "Mod inventory"));
                const mods = payload.mods || []; $("mods-table").innerHTML = `<thead><tr>${["ID",text("名称","Name"),text("版本","Version"),text("侧别","Side")].map(x=>`<th>${esc(x)}</th>`).join("")}</tr></thead><tbody>${mods.map(m=>`<tr>${cell(m.id)}${cell(m.name)}${cell(m.version)}${cell(m.side)}</tr>`).join("")}</tbody>`;
                const p = payload.profile; setText("profile-title", text("性能剖析", "Profile"));
                if (!p) { $("profile-summary").innerHTML = `<div class="muted">${text("报告未包含剖析数据。","No profile data in this report.")}</div>`; return; }
                const summary = [[text("时长","Duration"),format(p.durationSeconds)+" s"],[text("总采样","Total samples"),format(p.totalSamples)],[text("托管采样","Managed samples"),format(p.managedSamples)],[text("采样间隔","Interval"),format(p.intervalMs)+" ms"],[text("线程","Threads"),(p.threads||[]).length]];
                $("profile-summary").innerHTML = summary.map(([l,v])=>`<div class="tile"><div class="label">${esc(l)}</div><div class="value">${esc(v)}</div></div>`).join("");
                setText("mod-hotspots-title", text("Mod 热点","Mod hotspots")); setText("module-hotspots-title", text("模块热点","Module hotspots")); setText("threads-title", text("线程调用树","Thread call tree"));
                const list = items => (items||[]).map(x=>`<div>${esc(x.name)} <span class="tree-meta">${format(x.selfSamples)}</span></div>`).join("") || `<span class="muted">-</span>`; $("mod-hotspots").innerHTML=list(p.modHotspots); $("module-hotspots").innerHTML=list(p.moduleHotspots);
                const nodeHtml = (n, total) => { const share=total>0?n.totalSamples*100/total:0; const identity=[n.mod ? `Mod: ${n.mod}` : "", n.module ? `Module: ${n.module}` : ""].filter(Boolean).join(" · ") || "-"; const label=`${n.fullName||n.name||"-"} · ${identity}`; const children=(n.children||[]).sort((a,b)=>b.totalSamples-a.totalSamples); return `<details><summary>${esc(label)} <span class="tree-meta">${text("总","total")} ${format(n.totalSamples)} · ${text("自","self")} ${format(n.selfSamples)} · ${text("托管自","managed self")} ${format(n.selfManagedSamples)} · ${share.toFixed(1)}%</span></summary>${children.length?`<div class="tree">${children.map(cn=>nodeHtml(cn,total)).join("")}</div>`:""}</details>`; };
                $("threads").innerHTML=(p.threads||[]).sort((a,b)=>b.samples-a.samples).map(th=>`<details open><summary>${esc(th.name)} <span class="tree-meta">${text("总","total")} ${format(th.samples)} · ${text("托管","managed")} ${format(th.managedSamples)} · ${text("停泊","parked")} ${format(th.parkedSamples)}</span></summary><div class="tree">${(th.children||[]).sort((a,b)=>b.totalSamples-a.totalSamples).map(n=>nodeHtml(n,th.samples)).join("")}</div></details>`).join("") || `<span class="muted">-</span>`;
              };
              const formatDuration = seconds => { const n=Number(seconds)||0; if(n>=86400)return Math.floor(n/86400)+text("天","d"); if(n>=3600)return Math.floor(n/3600)+text("小时","h"); return Math.floor(n/60)+text("分","m")+Math.floor(n%60)+text("秒","s"); };
              const tierLabel = tier => tier.spanSeconds < 60 ? tier.spanSeconds + "s" : (tier.spanSeconds / 60) + "m";
              const notify = (type, detail) => { if (typeof invokeCSharpAction === "function") invokeCSharpAction(JSON.stringify({ type, detail })); };
              const currentTier = () => payload.tiers[state.tier] || null;
              const series = (tier, field) => (tier.values[field] || []).map(value => Number.isFinite(value) ? value : null);
              const hasValues = values => values.some(value => value !== null);
              const transformed = values => state.log ? values.map(value => value === null ? null : Math.log10(Math.max(0, value) + 1)) : values;
              const clearPlots = () => { state.plots.forEach(item => item.plot.destroy()); state.plots = []; };
              const updateReadout = (spec, lines, tier, index) => {
                if (index === null || index === undefined || index < 0 || index >= tier.times.length) { $("readout").textContent = t("将鼠标移动到图表上查看数值", "Move the pointer over a chart to inspect values"); return; }
                const values = lines.map(line => line.name + " " + format(line.raw[index])).join("  |  ");
                $("readout").textContent = timeText(tier.times[index]) + "  |  " + spec.title + "  |  " + values;
              };
              const buildPlot = (target, spec, tip) => {
                const tier = currentTier();
                const lines = spec.fields.map((field, index) => ({ field, name: spec.names[index], color: spec.colors[index], raw: series(tier, field) }));
                const data = [tier.times].concat(lines.map(line => transformed(line.raw)));
                const styles = getComputedStyle(document.documentElement);
                const axis = styles.getPropertyValue("--muted").trim();
                const grid = styles.getPropertyValue("--grid").trim();
                const options = {
                  width: Math.max(280, target.clientWidth),
                  height: Math.max(180, target.clientHeight),
                  cursor: { show: true, drag: { x: false, y: false, setScale: false } },
                  select: { show: true },
                  legend: { show: false },
                  scales: { x: { time: true }, y: { auto: true } },
                  axes: [
                    { stroke: axis, size: 38, space: 74, gap: 5, font: "10px Segoe UI", grid: { stroke: grid, width: 1 }, ticks: { stroke: axis, width: 1 }, values: (u, vals) => vals.map(v => axisTime(v, (tier.times[tier.times.length-1]||0)-tier.times[0])) },
                    { stroke: axis, size: 52, space: 42, gap: 5, font: "10px Segoe UI", grid: { stroke: grid, width: 1 }, ticks: { stroke: axis, width: 1 }, values: (u, vals) => vals.map(v => state.log ? format(Math.pow(10, v) - 1) : format(v)) }
                  ],
                  series: [{}].concat(lines.map(line => ({ label: line.name, stroke: line.color, width: 2, points: { show: false } }))),
                  hooks: { setCursor: [plot => { const index = plot.cursor.idx; updateReadout(spec, lines, tier, index); if (!tip || index === null || index === undefined || index < 0 || index >= tier.times.length) return; tip.innerHTML = `<strong>${esc(timeText(tier.times[index]))}</strong><br>${lines.map(line => `${esc(line.name)}: ${esc(format(line.raw[index]))}`).join("<br>")}`; tip.style.display = "block"; const left = Math.max(6, Math.min(target.clientWidth - tip.offsetWidth - 6, plot.cursor.left + 10)); tip.style.left = left + "px"; tip.style.top = "6px"; }] }
                };
                const plot = new uPlot(options, data, target);
                if (tip) target.addEventListener("mouseleave", () => { tip.style.display = "none"; });
                const item = { plot, spec, tier, lines, initialX: { min: plot.scales.x.min, max: plot.scales.x.max } };
                if (typeof ResizeObserver !== "undefined") new ResizeObserver(() => plot.setSize({ width: Math.max(280, target.clientWidth), height: Math.max(180, target.clientHeight) })).observe(target);
                return item;
              };
              const renderTierButtons = () => {
                const host = $("tiers"); host.replaceChildren();
                payload.tiers.forEach((tier, index) => {
                  const button = document.createElement("button"); button.type = "button"; button.textContent = tierLabel(tier); button.title = tier.count + " samples"; button.setAttribute("aria-pressed", String(index === state.tier)); button.disabled = !tier.times.length;
                  button.addEventListener("click", () => { state.tier = index; renderAll(); }); host.appendChild(button);
                });
              };
              const renderSeriesInventory = () => {
                const fields = [...new Set((payload.tiers || []).flatMap(tier => tier.fields || Object.keys(tier.values || {})))];
                if (!fields.length) return;
                const existing = $("series-title"); if (existing) existing.textContent = `${text("时间序列", "Time series")} · ${fields.length} ${text("项", "metrics")}`;
              };
              const renderTable = () => {
                const shell = $("table-shell"); shell.hidden = !state.table;
                if (!state.table) return;
                const tier = currentTier(); const table = $("data-table"); table.replaceChildren();
                const head = document.createElement("thead"); const row = document.createElement("tr"); [t("时间", "Time")].concat(metricFields).forEach(label => { const cell = document.createElement("th"); cell.textContent = label; row.appendChild(cell); }); head.appendChild(row); table.appendChild(head);
                const body = document.createElement("tbody");
                tier.times.forEach((timestamp, index) => { const tr = document.createElement("tr"); const time = document.createElement("td"); time.textContent = timeText(timestamp); tr.appendChild(time); metricFields.forEach(field => { const cell = document.createElement("td"); cell.textContent = format(series(tier, field)[index]); tr.appendChild(cell); }); body.appendChild(tr); });
                table.appendChild(body);
              };
              const renderAll = () => {
                const tier = currentTier(); clearPlots(); $("charts").replaceChildren(); renderTierButtons();
                if (!tier || !tier.times.length) { const empty = document.createElement("div"); empty.id = "empty"; empty.textContent = t("此报告没有可用的时间序列数据。", "This report has no usable time-series data."); $("charts").appendChild(empty); renderTable(); return; }
                specs.forEach(spec => {
                  const lines = spec.fields.map(field => series(tier, field)); if (!lines.some(hasValues)) return;
                  const card = document.createElement("section"); card.className = "chart"; card.tabIndex = 0;
                  const title = document.createElement("div"); title.className = "chart-title"; title.textContent = spec.title + (state.log ? " (log)" : "");
                  const subtitle = document.createElement("div"); subtitle.className = "chart-subtitle"; subtitle.textContent = `${tier.count || tier.times.length} ${t("个采样点", "samples")} · ${tier.times.length ? axisTime(tier.times[0], (tier.times[tier.times.length-1]||0)-tier.times[0]) + " – " + axisTime(tier.times[tier.times.length-1], (tier.times[tier.times.length-1]||0)-tier.times[0]) : "-"}`;
                  const shell = document.createElement("div"); shell.className = "plot-shell"; const target = document.createElement("div"); target.className = "plot"; const tip = document.createElement("div"); tip.className = "chart-tip"; shell.append(target, tip); card.append(title, subtitle, shell); $("charts").appendChild(card);
                  state.plots.push(buildPlot(target, spec, tip));
                });
                renderTable(); $("readout").textContent = t("将鼠标移动到图表上查看数值", "Move the pointer over a chart to inspect values"); notify("ready", state.plots.length + " charts, " + tierLabel(tier));
              };
              $("log").textContent = t("对数尺度", "Log scale"); $("table").textContent = t("表格视图", "Table view");
              $("log").addEventListener("click", () => { state.log = !state.log; $("log").setAttribute("aria-pressed", String(state.log)); renderAll(); });
              $("table").addEventListener("click", () => { state.table = !state.table; $("table").setAttribute("aria-pressed", String(state.table)); renderTable(); });
              document.addEventListener("keydown", event => { if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return; const item = state.plots[0]; if (!item) return; const current = item.plot.cursor.idx ?? 0; const index = Math.max(0, Math.min(item.tier.times.length - 1, current + (event.key === "ArrowLeft" ? -1 : 1))); item.plot.setCursor({ left: item.plot.valToPos(item.tier.times[index], "x"), top: item.plot.bbox.top + item.plot.bbox.height / 2 }); });
              renderAll();
              renderReport();
              renderSeriesInventory();
              document.body.dataset.lithosState = "ready";
            })();
            } catch (error) {
              reportFailure(error);
            }
            </script>
            </body>
            </html>
            """;
    }

    private static string ReadAssetText(string assetPath)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://LauncherGo.Ui/{assetPath}"));
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private sealed class BrowserPayload
    {
        public required bool IsChinese { get; init; }
        public int Schema { get; init; }
        public string Kind { get; init; } = string.Empty;
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public LithosProbeServerInfo Server { get; init; } = new();
        public LithosProbeCensus Census { get; init; } = new();
        public IReadOnlyList<LithosProbeHealthWindow> Windows { get; init; } = [];
        public IReadOnlyList<LithosProbeModInfo> Mods { get; init; } = [];
        public LithosProbeProfile? Profile { get; init; }
        public required IReadOnlyList<TrendTierPayload> Tiers { get; init; }
    }

    private sealed class TrendTierPayload
    {
        public required int SpanSeconds { get; init; }
        public required int Count { get; init; }
        public required IReadOnlyList<string> Fields { get; init; }
        public required IReadOnlyList<double> Times { get; init; }
        public required IReadOnlyDictionary<string, IReadOnlyList<double?>> Values { get; init; }
    }
}
