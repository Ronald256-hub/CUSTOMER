"use strict";

(function installExecutiveIntelligence() {
  const model = {
    fromDate: "",
    toDate: "",
    scope: "consolidated",
    data: null,
    rendering: false
  };

  const esc = (value) => String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");

  const rows = (value, keys = []) => {
    if (Array.isArray(value)) return value;
    for (const key of keys) {
      if (Array.isArray(value?.[key])) return value[key];
    }
    return [];
  };

  const number = (source, keys, fallback = 0) => {
    for (const key of keys) {
      const value = source?.[key];
      if (value !== undefined && value !== null && Number.isFinite(Number(value))) {
        return Number(value);
      }
    }
    return fallback;
  };

  const formatNumber = (value, digits = 0) => Number(value || 0).toLocaleString("en-UG", {
    maximumFractionDigits: digits
  });

  const money = (value) => `${formatNumber(Math.round(Number(value || 0)))} UGX`;
  const percent = (value) => `${formatNumber(value, 1)}%`;

  function localDate(date = new Date()) {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, "0");
    const day = String(date.getDate()).padStart(2, "0");
    return `${year}-${month}-${day}`;
  }

  function addDays(dateText, delta) {
    const value = new Date(`${dateText}T00:00:00`);
    value.setDate(value.getDate() + delta);
    return localDate(value);
  }

  function rangeFor(days) {
    const toDate = localDate();
    return { fromDate: addDays(toDate, -(days - 1)), toDate };
  }

  function initializeRange() {
    if (!model.fromDate || !model.toDate) Object.assign(model, rangeFor(30));
  }

  async function request(path) {
    try {
      return { ok: true, data: await api(path) };
    } catch (error) {
      if (error?.status === 401) throw error;
      return { ok: false, error, data: null };
    }
  }

  function metric(label, value, note, tone = "") {
    return `<article class="ei-metric ${esc(tone)}"><span>${esc(label)}</span><strong>${esc(value)}</strong><small>${esc(note)}</small></article>`;
  }

  function chip(label, tone = "neutral") {
    return `<span class="ei-chip ${esc(tone)}">${esc(label)}</span>`;
  }

  function empty(title, note) {
    return `<div class="ei-empty"><strong>${esc(title)}</strong><span>${esc(note)}</span></div>`;
  }

  function statusTone(level) {
    if (level === "critical") return "danger";
    if (level === "warning") return "warning";
    return "success";
  }

  function buildSignals(data) {
    const signals = [];
    const lowStock = data.inventory.filter((item) => item.isLowStock).length;
    const reorderCount = data.reorder.length;
    const shortGlassLow = data.shortGlass.filter((item) => item.isLowStock || Number(item.remainingGlasses || 0) <= 5).length;
    const overdueReceivables = data.receivables.filter((item) =>
      item.dueDate && new Date(`${item.dueDate}T23:59:59`) < new Date() && Number(item.outstandingAmountMinor || 0) > 0
    ).length;
    const margin = data.sales.netSalesMinor ? data.sales.grossProfitMinor / data.sales.netSalesMinor * 100 : 0;

    if (lowStock || reorderCount) signals.push({
      level: lowStock > 5 ? "critical" : "warning", mark: "ST",
      title: `${Math.max(lowStock, reorderCount)} stock line${Math.max(lowStock, reorderCount) === 1 ? "" : "s"} need replenishment`,
      note: "Review branch stock and procurement recommendations.", page: "inventory"
    });
    if (shortGlassLow) signals.push({
      level: "warning", mark: "SG",
      title: `${shortGlassLow} short-glass line${shortGlassLow === 1 ? "" : "s"} near the warning level`,
      note: "Sellable glasses remaining should be checked before the next busy period.", page: "inventory"
    });
    if (overdueReceivables) signals.push({
      level: overdueReceivables > 5 ? "critical" : "warning", mark: "AR",
      title: `${overdueReceivables} overdue customer balance${overdueReceivables === 1 ? "" : "s"}`,
      note: "Prioritise collection follow-ups in finance and CRM.", page: "finance"
    });
    if (data.summary.openShifts === 0) signals.push({
      level: "warning", mark: "SH", title: "No teller shift is open",
      note: "A shift must be open before point-of-sale transactions can be completed.", page: "sales"
    });
    if (data.sales.netSalesMinor > 0 && margin < 10) signals.push({
      level: "warning", mark: "GP", title: `Gross margin is ${percent(margin)}`,
      note: "Review cost prices, selling prices and discount discipline.", page: "reports"
    });
    if (!signals.length) signals.push({
      level: "healthy", mark: "OK", title: "No critical operating exception detected",
      note: "Sales, cash, stock, short-glass and workforce signals are within monitored thresholds.", page: "intelligence"
    });
    return signals;
  }

  function paymentRows(payments) {
    const total = payments.reduce((sum, item) => sum + Number(item.amountMinor || 0), 0);
    return payments.map((item) => {
      const amount = Number(item.amountMinor || 0);
      const share = total ? amount / total * 100 : 0;
      return `<article class="ei-payment-row"><div><strong>${esc(String(item.paymentMethod || "other").replaceAll("_", " "))}</strong><small>${formatNumber(item.saleCount)} sale${Number(item.saleCount) === 1 ? "" : "s"}</small></div><div class="ei-progress" aria-label="${esc(item.paymentMethod)} share ${formatNumber(share, 1)} percent"><span style="width:${Math.min(100, Math.max(0, share))}%"></span></div><div><strong>${money(amount)}</strong><small>${percent(share)}</small></div></article>`;
    }).join("");
  }

  function branchRows(shops) {
    const maxRevenue = Math.max(1, ...shops.map((item) => Number(item.grossSalesMinor || 0)));
    return shops.map((item) => {
      const revenue = Number(item.grossSalesMinor || 0);
      const profit = Number(item.grossProfitMinor || 0);
      const margin = revenue ? profit / revenue * 100 : 0;
      const width = revenue / maxRevenue * 100;
      return `<article class="ei-branch-row"><div class="ei-branch-name"><strong>${esc(item.shopName || item.shopCode)}</strong><small>${esc(item.shopCode || "")} · ${formatNumber(item.completedSalesCount)} sales</small></div><div class="ei-branch-bar"><span style="width:${Math.max(2, width)}%"></span></div><div><span>Revenue</span><strong>${money(revenue)}</strong></div><div><span>Gross profit</span><strong>${money(profit)}</strong></div><div><span>Margin</span><strong>${percent(margin)}</strong></div></article>`;
    }).join("");
  }

  function obligationRows(items, kind) {
    return items.slice(0, 8).map((item) => {
      const name = kind === "receivable" ? item.customerName : item.supplierName;
      const document = item.documentNumber || item.supplierInvoiceNumber || item.referenceNumber || "Open item";
      const due = item.dueDate || "No due date";
      return `<article class="ei-obligation-row"><div><strong>${esc(name || "Unassigned counterparty")}</strong><small>${esc(document)} · due ${esc(due)}</small></div><strong>${money(item.outstandingAmountMinor)}</strong></article>`;
    }).join("");
  }

  function shortGlassRows(items) {
    return items.slice(0, 10).map((item) => `<article class="ei-short-row"><div><strong>${esc(item.productName)}</strong><small>${esc(item.sku)} · ${formatNumber(item.glassSizeMl)} ml glass</small></div><div><span>Sold</span><strong>${formatNumber(item.glassesSold)} glasses</strong></div><div><span>Dispensed</span><strong>${formatNumber(item.volumeDispensedMl)} ml</strong></div><div><span>Revenue</span><strong>${money(item.revenueMinor)}</strong></div><div><span>Remaining</span><strong>${formatNumber(item.remainingGlasses)} glasses</strong></div>${chip(item.isLowStock ? "Low" : "Ready", item.isLowStock ? "danger" : "success")}</article>`).join("");
  }

  async function load() {
    initializeRange();
    const toExclusive = addDays(model.toDate, 1);
    const salesQuery = new URLSearchParams({ scope: model.scope, fromUtc: `${model.fromDate}T00:00:00.000Z`, toUtc: `${toExclusive}T00:00:00.000Z` });
    const shortQuery = new URLSearchParams({ fromDate: model.fromDate, toDate: model.toDate });
    const [salesResult, summaryResult, inventoryResult, shortGlassResult, receivablesResult, payablesResult, cashbookResult, reorderResult, crmResult, hrmResult] = await Promise.all([
      request(`/api/v3/reports/sales/summary?${salesQuery}`), request("/api/v3/admin/summary"), request("/api/v3/admin/inventory/products"),
      request(`/api/v3/reports/short-glass?${shortQuery}`), request("/api/v3/finance/receivables?status=open&limit=100"),
      request("/api/v3/finance/payables?status=open&limit=100"), request("/api/v3/finance/cashbook?scope=shop&limit=100"),
      request("/api/v3/procurement/reorder-recommendations"), request("/api/v3/crm/dashboard"), request("/api/v3/hrm/dashboard")
    ]);
    const sales = salesResult.data || {};
    const summary = summaryResult.data || {};
    const inventory = rows(inventoryResult.data, ["products"]);
    const shortReport = shortGlassResult.data || {};
    const shortGlass = rows(shortReport, ["products"]);
    const receivablesData = receivablesResult.data || {};
    const payablesData = payablesResult.data || {};
    const cashbookData = cashbookResult.data || {};
    const reorderData = reorderResult.data || {};
    const crm = crmResult.data || {};
    const hrm = hrmResult.data || {};
    model.data = {
      sales: {
        completedSalesCount: number(sales, ["completedSalesCount", "salesCount"]), voidedSalesCount: number(sales, ["voidedSalesCount"]),
        grossSalesMinor: number(sales, ["grossSalesMinor"]), netSalesMinor: number(sales, ["netSalesMinor", "grossSalesMinor"]),
        costOfGoodsSoldMinor: number(sales, ["costOfGoodsSoldMinor"]), grossProfitMinor: number(sales, ["grossProfitMinor"]),
        shops: rows(sales, ["shops"]), payments: rows(sales, ["payments"])
      },
      summary: {
        activeProducts: number(summary, ["activeProducts"], inventory.filter((item) => item.isActive !== false).length),
        lowStockProducts: number(summary, ["lowStockProducts"], inventory.filter((item) => item.isLowStock).length),
        activeUsers: number(summary, ["activeUsers"]), openShifts: number(summary, ["openShifts"]), savedDocuments: number(summary, ["savedDocuments"])
      },
      inventory, shortReport, shortGlass, receivablesData, receivables: rows(receivablesData, ["receivables"]),
      payablesData, payables: rows(payablesData, ["payables"]), cashbookData, cashbook: rows(cashbookData, ["entries", "cashbook"]),
      reorderData, reorder: rows(reorderData, ["recommendations", "items"]), crm, hrm,
      availability: [salesResult, summaryResult, inventoryResult, shortGlassResult, receivablesResult, payablesResult, cashbookResult, reorderResult, crmResult, hrmResult].filter((item) => item.ok).length
    };
  }

  function renderWorkspace() {
    const data = model.data;
    const sales = data.sales;
    const margin = sales.netSalesMinor ? sales.grossProfitMinor / sales.netSalesMinor * 100 : 0;
    const averageSale = sales.completedSalesCount ? sales.netSalesMinor / sales.completedSalesCount : 0;
    const receivableOutstanding = number(data.receivablesData, ["outstandingMinor", "outstandingAmountMinor"], data.receivables.reduce((sum, item) => sum + Number(item.outstandingAmountMinor || 0), 0));
    const payableOutstanding = number(data.payablesData, ["outstandingMinor", "outstandingAmountMinor"], data.payables.reduce((sum, item) => sum + Number(item.outstandingAmountMinor || 0), 0));
    const cashMovement = number(data.cashbookData, ["netMovementMinor"], data.cashbook.reduce((sum, item) => sum + Number(item.signedAmountMinor || 0), 0));
    const signals = buildSignals(data);
    const healthTone = data.availability === 10 ? "success" : data.availability >= 7 ? "warning" : "danger";
    document.querySelector("#page").innerHTML = `<div class="executive-intelligence-workspace">
      <section class="ei-hero"><div><span class="ei-eyebrow">NEXUS POS 6.5 · EXECUTIVE INTELLIGENCE</span><h2>Executive intelligence control tower</h2><p>One read-only command surface for revenue, gross profit, payment mix, debt, stock risk, short-glass quantities, customers and workforce readiness.</p></div><div class="ei-hero-actions"><button type="button" data-ei-export>Export intelligence CSV</button><button type="button" data-ei-print>Print control tower</button></div></section>
      <form id="executiveIntelligenceFilters" class="panel ei-filters"><div><span>Period presets</span><div class="ei-presets"><button type="button" data-ei-days="1">Today</button><button type="button" data-ei-days="7">Last 7 days</button><button type="button" data-ei-days="30">Last 30 days</button><button type="button" data-ei-days="90">Last 90 days</button></div></div><label>Reporting scope<select name="scope" aria-label="Reporting scope"><option value="consolidated" ${model.scope === "consolidated" ? "selected" : ""}>All branches</option><option value="shop" ${model.scope === "shop" ? "selected" : ""}>Active branch</option></select></label><label>From date<input name="fromDate" type="date" value="${esc(model.fromDate)}" required></label><label>To date<input name="toDate" type="date" value="${esc(model.toDate)}" required></label><button class="primary" type="submit">Refresh intelligence</button></form>
      <section class="ei-metrics" aria-label="Executive metrics">${metric("Net sales", money(sales.netSalesMinor), `${formatNumber(sales.completedSalesCount)} completed transactions`, "success")}${metric("Gross profit", money(sales.grossProfitMinor), `${percent(margin)} gross margin`, margin >= 20 ? "blue" : "warning")}${metric("Average sale", money(averageSale), `${formatNumber(sales.voidedSalesCount)} voided transaction${sales.voidedSalesCount === 1 ? "" : "s"}`, "blue")}${metric("Customer debt", money(receivableOutstanding), `${formatNumber(data.receivables.length)} open receivable${data.receivables.length === 1 ? "" : "s"}`, receivableOutstanding ? "warning" : "success")}${metric("Supplier obligations", money(payableOutstanding), `${formatNumber(data.payables.length)} open payable${data.payables.length === 1 ? "" : "s"}`, payableOutstanding ? "warning" : "success")}${metric("Cash movement", money(cashMovement), `${formatNumber(data.cashbook.length)} recent cashbook entries`, cashMovement >= 0 ? "success" : "warning")}${metric("Stock risk", formatNumber(Math.max(data.summary.lowStockProducts, data.reorder.length)), `${formatNumber(data.summary.activeProducts)} active products`, data.summary.lowStockProducts ? "warning" : "success")}${metric("Data coverage", `${data.availability}/10`, "Reporting services responding", healthTone)}</section>
      <section class="ei-layout"><article class="panel ei-wide"><div class="ei-section-head"><div><h3>Business performance pulse</h3><p>Branch revenue, gross profit and margin for the selected period.</p></div>${chip(model.scope === "consolidated" ? "All branches" : "Active branch", "blue")}</div><div class="ei-branch-list">${sales.shops.length ? branchRows(sales.shops) : empty("No branch sales in this period", "Complete a sale or broaden the reporting period.")}</div></article><article class="panel"><div class="ei-section-head"><div><h3>Payment mix</h3><p>How completed sales were collected.</p></div></div><div class="ei-payment-list">${sales.payments.length ? paymentRows(sales.payments) : empty("No payment activity", "No completed payment was recorded in this period.")}</div></article><article class="panel"><div class="ei-section-head"><div><h3>Risk radar</h3><p>Exceptions requiring management attention.</p></div>${chip(`${signals.length} signal${signals.length === 1 ? "" : "s"}`, signals.every((item) => item.level === "healthy") ? "success" : "warning")}</div><div class="ei-signal-list">${signals.map((item) => `<button type="button" class="ei-signal ${statusTone(item.level)}" data-page="${esc(item.page)}"><span>${esc(item.mark)}</span><span><strong>${esc(item.title)}</strong><small>${esc(item.note)}</small></span><span aria-hidden="true">→</span></button>`).join("")}</div></article></section>
      <section class="ei-layout"><article class="panel"><div class="ei-section-head"><div><h3>Customer collections</h3><p>Largest currently open customer balances.</p></div><button type="button" data-page="finance">Open finance</button></div><div class="ei-obligations">${data.receivables.length ? obligationRows(data.receivables, "receivable") : empty("No customer debt", "There are no open customer receivables.")}</div></article><article class="panel"><div class="ei-section-head"><div><h3>Supplier commitments</h3><p>Largest currently open supplier obligations.</p></div><button type="button" data-page="finance">Open payables</button></div><div class="ei-obligations">${data.payables.length ? obligationRows(data.payables, "payable") : empty("No supplier obligations", "There are no open supplier payables.")}</div></article></section>
      <section class="panel"><div class="ei-section-head"><div><h3>Short-glass quantity and revenue watch</h3><p>Actual glasses sold, millilitres dispensed and current sellable balance for the active branch.</p></div>${chip(`${formatNumber(number(data.shortReport, ["totalRemainingGlasses"]))} glasses remaining`, data.shortGlass.some((item) => item.isLowStock) ? "warning" : "success")}</div><div class="ei-short-list">${data.shortGlass.length ? shortGlassRows(data.shortGlass) : empty("No short-glass products", "Configure a short-glass product to activate measured liquid monitoring.")}</div></section>
      <section class="ei-layout"><article class="panel"><div class="ei-section-head"><div><h3>Customer growth pulse</h3><p>CRM workload and pipeline readiness.</p></div><button type="button" data-page="crm">Open CRM</button></div><div class="ei-mini-metrics">${metric("Active customers", formatNumber(number(data.crm, ["activeCustomerCount", "customerCount"])), "Current CRM base")}${metric("Overdue follow-ups", formatNumber(number(data.crm, ["overdueTaskCount"])), "Actions past due", number(data.crm, ["overdueTaskCount"]) ? "warning" : "success")}${metric("Open quotations", formatNumber(number(data.crm, ["openQuotationCount"])), money(number(data.crm, ["openQuotationValueMinor"])), "blue")}</div></article><article class="panel"><div class="ei-section-head"><div><h3>Workforce readiness</h3><p>Attendance, leave and payroll preparation.</p></div><button type="button" data-page="hrm">Open HRM</button></div><div class="ei-mini-metrics">${metric("Active employees", formatNumber(number(data.hrm, ["activeEmployeeCount"])), "Current workforce")}${metric("Attendance today", formatNumber(number(data.hrm, ["todayAttendanceCount"])), `${formatNumber(number(data.hrm, ["openAttendanceCount"]))} open records`, "blue")}${metric("Pending leave", formatNumber(number(data.hrm, ["pendingLeaveRequestCount"])), `${formatNumber(number(data.hrm, ["draftPayrollPeriodCount"]))} draft payroll periods`, number(data.hrm, ["pendingLeaveRequestCount"]) ? "warning" : "success")}</div></article></section>
    </div>`;
  }

  async function render() {
    if (model.rendering) return;
    model.rendering = true;
    const page = document.querySelector("#page");
    if (!page) { model.rendering = false; return; }
    page.innerHTML = `<div class="page-loading" aria-live="polite" aria-busy="true"><div class="skeleton"></div><div class="skeleton" style="min-height:420px"></div></div>`;
    try { await load(); renderWorkspace(); }
    catch (error) {
      if (typeof window.handleError === "function") window.handleError(error);
      page.innerHTML = `<section class="panel"><h2>Executive intelligence could not load</h2><p>${esc(error?.message || "The reporting services are unavailable.")}</p></section>`;
    } finally { model.rendering = false; }
  }

  function exportCsv() {
    if (!model.data) return;
    const lines = [["Nexus POS executive intelligence"], ["From", model.fromDate], ["To", model.toDate], ["Scope", model.scope], [], ["Branch", "Code", "Completed sales", "Gross sales UGX", "COGS UGX", "Gross profit UGX"], ...model.data.sales.shops.map((item) => [item.shopName, item.shopCode, item.completedSalesCount, item.grossSalesMinor, item.costOfGoodsSoldMinor, item.grossProfitMinor]), [], ["Payment method", "Sales", "Amount UGX"], ...model.data.sales.payments.map((item) => [item.paymentMethod, item.saleCount, item.amountMinor])];
    const csv = lines.map((line) => line.map((value) => `"${String(value ?? "").replaceAll('"', '""')}"`).join(",")).join("\r\n");
    const blob = new Blob([csv], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `nexus-executive-intelligence-${model.fromDate}-to-${model.toDate}.csv`;
    document.body.appendChild(link); link.click(); link.remove(); URL.revokeObjectURL(url);
  }

  document.addEventListener("submit", (event) => {
    if (!event.target.matches("#executiveIntelligenceFilters")) return;
    event.preventDefault();
    const values = Object.fromEntries(new FormData(event.target).entries());
    if (values.toDate < values.fromDate) { showMessage("The intelligence end date cannot be before the start date.", true); return; }
    model.fromDate = String(values.fromDate); model.toDate = String(values.toDate); model.scope = String(values.scope); render();
  });

  document.addEventListener("click", (event) => {
    const preset = event.target.closest("[data-ei-days]");
    if (preset) { Object.assign(model, rangeFor(Number(preset.dataset.eiDays))); render(); return; }
    if (event.target.closest("[data-ei-export]")) { exportCsv(); return; }
    if (event.target.closest("[data-ei-print]")) window.print();
  });

  window.NexusExecutiveIntelligence = { render, isRendering: () => model.rendering };
})();
