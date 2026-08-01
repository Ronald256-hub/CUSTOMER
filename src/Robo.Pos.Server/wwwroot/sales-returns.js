"use strict";

(function installSalesReturnsWorkspace() {
  const state = {
    loading: false,
    eligible: [],
    selected: null,
    returns: [],
    query: ""
  };

  const esc = (value) => typeof window.escapeHtml === "function"
    ? window.escapeHtml(String(value ?? ""))
    : String(value ?? "").replaceAll("&", "&amp;").replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;").replaceAll('"', "&quot;").replaceAll("'", "&#039;");
  const number = (value) => Number(value || 0).toLocaleString("en-UG");
  const money = (value) => `${number(value)} UGX`;
  const dateTime = (value) => {
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? String(value || "") : parsed.toLocaleString("en-UG", { dateStyle: "medium", timeStyle: "short" });
  };
  const method = (value) => String(value || "").replaceAll("_", " ").replace(/\b\w/g, (letter) => letter.toUpperCase());

  function notify(message, error = false) {
    const host = document.querySelector("#message");
    if (!host) return;
    host.textContent = message;
    host.classList.remove("hidden");
    host.classList.toggle("error", error);
    clearTimeout(notify.timer);
    notify.timer = setTimeout(() => host.classList.add("hidden"), 5000);
  }

  async function safe(path, options) {
    try {
      return { ok: true, data: await api(path, options) };
    } catch (error) {
      return { ok: false, data: {}, error };
    }
  }

  function filteredEligible() {
    const query = state.query.trim().toLowerCase();
    if (!query) return state.eligible;
    return state.eligible.filter((sale) =>
      `${sale.receiptNumber} ${sale.invoiceNumber || ""} ${sale.customerName || ""}`.toLowerCase().includes(query));
  }

  function saleCard(sale) {
    const selected = state.selected?.saleId === sale.saleId;
    return `<button type="button" class="sr-sale-card ${selected ? "selected" : ""}" data-return-sale="${esc(sale.saleId)}" aria-pressed="${selected}">
      <span class="sr-sale-card-head"><strong>${esc(sale.receiptNumber)}</strong><span>${money(sale.remainingAmountMinor)}</span></span>
      <span>${esc(sale.customerName || "Walk-in customer")} · ${esc(method(sale.paymentMethod))}</span>
      <small>${esc(dateTime(sale.completedAtUtc))} · ${number(sale.remainingQuantity)} item${Number(sale.remainingQuantity) === 1 ? "" : "s"} remaining</small>
      ${Number(sale.returnedAmountMinor) > 0 ? `<em>${money(sale.returnedAmountMinor)} already refunded</em>` : ""}
    </button>`;
  }

  function returnLine(line) {
    return `<article class="sr-line" data-return-line="${esc(line.saleItemId)}">
      <label class="sr-line-select">
        <input type="checkbox" data-return-check="${esc(line.saleItemId)}">
        <span><strong>${esc(line.productName)}</strong><small>${esc(line.sku)} · sold ${number(line.soldQuantity)} · returned ${number(line.returnedQuantity)}</small></span>
      </label>
      <label><span>Return quantity</span><input type="number" min="1" max="${esc(line.remainingQuantity)}" value="${esc(line.remainingQuantity)}" data-return-quantity="${esc(line.saleItemId)}" disabled></label>
      <label><span>Stock disposition</span><select data-return-disposition="${esc(line.saleItemId)}" disabled><option value="restock">Resellable — restore stock</option><option value="damaged">Damaged — do not restore</option></select></label>
      <div class="sr-line-value"><span>Maximum refund</span><strong>${money(line.remainingRefundMinor)}</strong><small>${number(line.remainingQuantity)} ${esc(line.saleUnit)} available</small></div>
    </article>`;
  }

  function selectedPanel() {
    const sale = state.selected;
    if (!sale) {
      return `<section class="panel sr-return-panel sr-empty"><strong>Select an eligible receipt</strong><span>The original receipt remains immutable. A separate audited credit note will be created.</span></section>`;
    }
    return `<section class="panel sr-return-panel">
      <div class="sr-panel-head"><div><span class="workspace-eyebrow">ORIGINAL SALE</span><h2>${esc(sale.receiptNumber)}</h2><p>${esc(sale.customerName || "Walk-in customer")} · ${esc(dateTime(sale.completedAtUtc))}</p></div><div><span>Refund channel</span><strong>${esc(method(sale.paymentMethod))}</strong><small>Must match original payment</small></div></div>
      <form id="salesReturnForm" class="sr-form">
        <div class="sr-lines">${sale.items.length ? sale.items.map(returnLine).join("") : '<div class="workspace-empty"><strong>No returnable items</strong><span>Every line on this receipt has already been returned.</span></div>'}</div>
        <div class="sr-form-bottom">
          <div class="sr-reason-fields">
            <label><span>Return reason</span><textarea name="reason" rows="3" minlength="5" maxlength="500" required placeholder="Explain why the customer is returning the items"></textarea></label>
            <label><span>Internal notes</span><textarea name="notes" rows="2" maxlength="500" placeholder="Optional inspection or approval details"></textarea></label>
          </div>
          <aside class="sr-refund-summary">
            <span>Calculated refund</span><strong id="salesReturnRefundTotal">0 UGX</strong>
            <small id="salesReturnSelectionCount">No lines selected</small>
            <button class="primary full" type="submit" ${sale.items.length ? "" : "disabled"}>Complete refund and credit note</button>
            <p>Administrator approval, open shift and open accounting period are required.</p>
          </aside>
        </div>
      </form>
    </section>`;
  }

  function creditNoteLinks(record) {
    const documents = Array.isArray(record.documents) ? record.documents : [];
    const html = documents.find((item) => item.fileFormat === "html");
    const json = documents.find((item) => item.fileFormat === "json");
    return `<div class="sr-documents">
      ${html ? `<a href="/api/v3/sales/returns/${encodeURIComponent(record.id)}/documents/${encodeURIComponent(html.id)}" target="_blank" rel="noopener">Open credit note</a>` : ""}
      ${json ? `<a href="/api/v3/sales/returns/${encodeURIComponent(record.id)}/documents/${encodeURIComponent(json.id)}">Audit JSON</a>` : ""}
    </div>`;
  }

  function recentReturns() {
    return `<section class="panel sr-history-panel">
      <div class="sr-panel-head"><div><h2>Recent credit notes</h2><p>Immutable completed returns for the active branch.</p></div><span>${number(state.returns.length)} records</span></div>
      <div class="sr-history-list">${state.returns.length ? state.returns.map((record) => `<article class="sr-history-row">
        <div><strong>${esc(record.returnNumber)}</strong><small>Receipt ${esc(record.originalReceiptNumber)} · ${esc(dateTime(record.completedAtUtc))}</small><span>${esc(record.reason)}</span></div>
        <div><strong>${money(record.refundAmountMinor)}</strong><small>${esc(method(record.refundMethod))}</small>${creditNoteLinks(record)}</div>
      </article>`).join("") : '<div class="workspace-empty"><strong>No credit notes yet</strong><span>Completed customer returns will appear here.</span></div>'}</div>
    </section>`;
  }

  function render() {
    const page = document.querySelector("#page");
    if (!page) return;
    const eligible = filteredEligible();
    const totalExposure = state.eligible.reduce((sum, sale) => sum + Number(sale.remainingAmountMinor || 0), 0);
    const returnedTotal = state.returns.reduce((sum, record) => sum + Number(record.refundAmountMinor || 0), 0);
    page.dataset.salesReturnsWorkspace = "1";
    page.innerHTML = `<div class="sales-returns-workspace">
      <section class="sr-hero"><div><span class="workspace-eyebrow">CONTROLLED AFTER-SALES</span><h2>Sales returns and refunds</h2><p>Return selected receipt lines, preserve the original sale, restore only resellable stock and issue an immutable credit note.</p></div><button type="button" data-page="receipts">Open receipt archive</button></section>
      <section class="sr-metrics" aria-label="Sales return metrics">
        <article><span>Eligible receipts</span><strong>${number(state.eligible.length)}</strong><small>Active branch only</small></article>
        <article><span>Returnable value</span><strong>${money(totalExposure)}</strong><small>Before any new refund</small></article>
        <article><span>Completed credit notes</span><strong>${number(state.returns.length)}</strong><small>${money(returnedTotal)} refunded</small></article>
        <article><span>Accounting mode</span><strong>Atomic</strong><small>Revenue, payment, COGS and stock</small></article>
      </section>
      <section class="sr-layout">
        <aside class="panel sr-sale-list-panel">
          <div class="sr-panel-head"><div><h2>Eligible receipts</h2><p>Completed non-credit sales with quantities remaining.</p></div></div>
          <label class="sr-search"><span>Find receipt</span><input id="salesReturnSearch" type="search" value="${esc(state.query)}" placeholder="Receipt, invoice or customer" autocomplete="off"></label>
          <div class="sr-sale-list">${eligible.length ? eligible.map(saleCard).join("") : '<div class="workspace-empty"><strong>No eligible receipts</strong><span>No matching completed sale has a remaining returnable line.</span></div>'}</div>
        </aside>
        ${selectedPanel()}
      </section>
      ${recentReturns()}
    </div>`;
    updateSummary();
  }

  async function load() {
    if (state.loading) return;
    state.loading = true;
    const page = document.querySelector("#page");
    if (page) page.innerHTML = '<div class="page-loading"><div class="skeleton"></div><div class="skeleton" style="min-height:420px"></div></div>';
    const [eligible, history] = await Promise.all([
      safe("/api/v3/sales/returns/eligible?limit=100"),
      safe("/api/v3/sales/returns?limit=50")
    ]);
    state.eligible = Array.isArray(eligible.data?.sales) ? eligible.data.sales : [];
    state.returns = Array.isArray(history.data?.returns) ? history.data.returns : [];
    if (!eligible.ok) notify(eligible.error?.message || "Eligible receipts could not be loaded.", true);
    if (!history.ok) notify(history.error?.message || "Credit-note history could not be loaded.", true);
    state.loading = false;
    render();
  }

  async function selectSale(saleId) {
    const result = await safe(`/api/v3/sales/${encodeURIComponent(saleId)}/returnable`);
    if (!result.ok) {
      notify(result.error?.message || "The receipt could not be prepared for return.", true);
      return;
    }
    state.selected = result.data;
    render();
  }

  function selectedRequestLines() {
    if (!state.selected) return [];
    return state.selected.items.flatMap((line) => {
      const check = document.querySelector(`[data-return-check="${CSS.escape(String(line.saleItemId))}"]`);
      if (!check?.checked) return [];
      const quantity = Number(document.querySelector(`[data-return-quantity="${CSS.escape(String(line.saleItemId))}"]`)?.value || 0);
      const disposition = document.querySelector(`[data-return-disposition="${CSS.escape(String(line.saleItemId))}"]`)?.value || "restock";
      return [{ saleItemId: line.saleItemId, quantity, disposition }];
    });
  }

  function updateSummary() {
    if (!state.selected) return;
    const requestLines = selectedRequestLines();
    let total = 0;
    requestLines.forEach((selected) => {
      const line = state.selected.items.find((item) => Number(item.saleItemId) === Number(selected.saleItemId));
      if (!line || selected.quantity <= 0) return;
      total += selected.quantity === Number(line.remainingQuantity)
        ? Number(line.remainingRefundMinor)
        : Math.floor(Number(line.remainingRefundMinor) * selected.quantity / Number(line.remainingQuantity));
    });
    const totalHost = document.querySelector("#salesReturnRefundTotal");
    const countHost = document.querySelector("#salesReturnSelectionCount");
    if (totalHost) totalHost.textContent = money(total);
    if (countHost) countHost.textContent = requestLines.length
      ? `${number(requestLines.length)} line${requestLines.length === 1 ? "" : "s"} selected`
      : "No lines selected";
  }

  document.addEventListener("click", (event) => {
    const sale = event.target.closest("[data-return-sale]");
    if (sale) {
      selectSale(sale.dataset.returnSale);
    }
  });

  document.addEventListener("input", (event) => {
    if (event.target.id === "salesReturnSearch") {
      state.query = event.target.value;
      render();
      document.querySelector("#salesReturnSearch")?.focus();
    }
    if (event.target.matches("[data-return-quantity]")) updateSummary();
  });

  document.addEventListener("change", (event) => {
    if (event.target.matches("[data-return-check]")) {
      const id = event.target.dataset.returnCheck;
      document.querySelector(`[data-return-quantity="${CSS.escape(id)}"]`)?.toggleAttribute("disabled", !event.target.checked);
      document.querySelector(`[data-return-disposition="${CSS.escape(id)}"]`)?.toggleAttribute("disabled", !event.target.checked);
      updateSummary();
    }
    if (event.target.matches("[data-return-disposition]")) updateSummary();
  });

  document.addEventListener("submit", async (event) => {
    if (event.target.id !== "salesReturnForm") return;
    event.preventDefault();
    if (!state.selected) return;
    const items = selectedRequestLines();
    if (!items.length || items.some((item) => item.quantity <= 0)) {
      notify("Select at least one item and enter a valid return quantity.", true);
      return;
    }
    const values = Object.fromEntries(new FormData(event.target));
    const submit = event.target.querySelector('button[type="submit"]');
    submit.disabled = true;
    submit.textContent = "Processing controlled refund…";
    const result = await safe(`/api/v3/sales/${encodeURIComponent(state.selected.saleId)}/returns`, {
      method: "POST",
      body: JSON.stringify({
        items,
        refundMethod: state.selected.paymentMethod,
        reason: values.reason,
        notes: values.notes
      })
    });
    if (!result.ok) {
      submit.disabled = false;
      submit.textContent = "Complete refund and credit note";
      notify(result.error?.message || "The sales return could not be completed.", true);
      return;
    }
    notify(`Credit note ${result.data.returnNumber} completed for ${money(result.data.refundAmountMinor)}.`);
    state.selected = null;
    await load();
  });

  window.NexusSalesReturns = {
    render: load,
    isRendering: () => state.loading
  };
})();
