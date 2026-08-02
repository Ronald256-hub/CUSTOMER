"use strict";

(function installCreditReturnsWorkspace() {
  const state = {
    loading: false,
    tab: "returns",
    eligible: [],
    selected: null,
    returns: [],
    credits: [],
    applications: [],
    receivables: [],
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
    return Number.isNaN(parsed.getTime())
      ? String(value || "")
      : parsed.toLocaleString("en-UG", { dateStyle: "medium", timeStyle: "short" });
  };
  const today = () => new Date().toISOString().slice(0, 10);

  function notify(message, error = false) {
    const host = document.querySelector("#message");
    if (!host) return;
    host.textContent = message;
    host.classList.remove("hidden");
    host.classList.toggle("error", error);
    clearTimeout(notify.timer);
    notify.timer = setTimeout(() => host.classList.add("hidden"), 5500);
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
      `${sale.receiptNumber} ${sale.invoiceNumber || ""} ${sale.customerNumber} ${sale.customerName}`
        .toLowerCase().includes(query));
  }

  function tabButton(id, label, count) {
    const selected = state.tab === id;
    return `<button type="button" role="tab" aria-selected="${selected}" class="cr-tab ${selected ? "active" : ""}" data-credit-tab="${id}">${esc(label)}<span>${number(count)}</span></button>`;
  }

  function saleCard(sale) {
    const selected = state.selected?.saleId === sale.saleId;
    return `<button type="button" class="cr-sale-card ${selected ? "selected" : ""}" data-credit-sale="${esc(sale.saleId)}" aria-pressed="${selected}">
      <span class="cr-card-head"><strong>${esc(sale.invoiceNumber || sale.receiptNumber)}</strong><span>${money(sale.remainingReturnAmountMinor)}</span></span>
      <span>${esc(sale.customerNumber)} · ${esc(sale.customerName)}</span>
      <small>${number(sale.remainingQuantity)} item${Number(sale.remainingQuantity) === 1 ? "" : "s"} returnable · ${esc(dateTime(sale.completedAtUtc))}</small>
      <em>${money(sale.receivableOutstandingAmountMinor)} currently outstanding</em>
    </button>`;
  }

  function returnLine(line) {
    return `<article class="cr-line" data-credit-line="${esc(line.saleItemId)}">
      <label class="cr-line-check">
        <input type="checkbox" data-credit-return-check="${esc(line.saleItemId)}">
        <span><strong>${esc(line.productName)}</strong><small>${esc(line.sku)} · sold ${number(line.soldQuantity)} · returned ${number(line.returnedQuantity)}</small></span>
      </label>
      <label><span>Return quantity</span><input type="number" min="1" max="${esc(line.remainingQuantity)}" value="${esc(line.remainingQuantity)}" data-credit-return-quantity="${esc(line.saleItemId)}" disabled></label>
      <label><span>Stock disposition</span><select data-credit-return-disposition="${esc(line.saleItemId)}" disabled><option value="restock">Resellable — restore stock</option><option value="damaged">Damaged — no stock restoration</option></select></label>
      <div class="cr-line-value"><span>Maximum credit</span><strong>${money(line.remainingRefundMinor)}</strong><small>${number(line.remainingQuantity)} ${esc(line.saleUnit)} available</small></div>
    </article>`;
  }

  function selectedRequestLines() {
    if (!state.selected) return [];
    return state.selected.items.flatMap((line) => {
      const key = CSS.escape(String(line.saleItemId));
      const checked = document.querySelector(`[data-credit-return-check="${key}"]`)?.checked;
      if (!checked) return [];
      return [{
        saleItemId: line.saleItemId,
        quantity: Number(document.querySelector(`[data-credit-return-quantity="${key}"]`)?.value || 0),
        disposition: document.querySelector(`[data-credit-return-disposition="${key}"]`)?.value || "restock"
      }];
    });
  }

  function selectedValue() {
    if (!state.selected) return 0;
    return selectedRequestLines().reduce((sum, selected) => {
      const line = state.selected.items.find((item) => Number(item.saleItemId) === Number(selected.saleItemId));
      if (!line || selected.quantity <= 0) return sum;
      const value = selected.quantity === Number(line.remainingQuantity)
        ? Number(line.remainingRefundMinor)
        : Math.floor(Number(line.remainingRefundMinor) * selected.quantity / Number(line.remainingQuantity));
      return sum + value;
    }, 0);
  }

  function updateReturnSummary() {
    if (!state.selected) return;
    const total = selectedValue();
    const receivable = Math.min(total, Number(state.selected.receivableOutstandingAmountMinor || 0));
    const customerCredit = Math.max(0, total - receivable);
    const lines = selectedRequestLines();
    const totalHost = document.querySelector("#creditReturnTotal");
    const receivableHost = document.querySelector("#creditReturnReceivable");
    const creditHost = document.querySelector("#creditReturnCustomerCredit");
    const countHost = document.querySelector("#creditReturnSelectionCount");
    if (totalHost) totalHost.textContent = money(total);
    if (receivableHost) receivableHost.textContent = money(receivable);
    if (creditHost) creditHost.textContent = money(customerCredit);
    if (countHost) countHost.textContent = lines.length
      ? `${number(lines.length)} line${lines.length === 1 ? "" : "s"} selected`
      : "No lines selected";
  }

  function selectedReturnPanel() {
    const sale = state.selected;
    if (!sale) {
      return `<section class="panel cr-return-panel cr-empty"><strong>Select an eligible credit invoice</strong><span>Nexus will reduce the unpaid receivable first and create customer credit only for any excess.</span></section>`;
    }
    return `<section class="panel cr-return-panel">
      <div class="cr-panel-head"><div><span class="workspace-eyebrow">CREDIT INVOICE</span><h2>${esc(sale.invoiceNumber || sale.receiptNumber)}</h2><p>${esc(sale.customerNumber)} · ${esc(sale.customerName)}</p></div><div><span>Outstanding receivable</span><strong>${money(sale.receivableOutstandingAmountMinor)}</strong><small>Original invoice ${money(sale.receivableOriginalAmountMinor)}</small></div></div>
      <form id="creditReturnForm" class="cr-form">
        <div class="cr-lines">${sale.items.length ? sale.items.map(returnLine).join("") : '<div class="workspace-empty"><strong>No returnable items</strong><span>Every line has already been returned.</span></div>'}</div>
        <div class="cr-form-bottom">
          <div class="cr-reason-fields">
            <label><span>Credit-return reason</span><textarea name="reason" rows="3" minlength="5" maxlength="500" required placeholder="Explain why the goods are being returned"></textarea></label>
            <label><span>Internal notes</span><textarea name="notes" rows="2" maxlength="500" placeholder="Optional inspection, approval or customer details"></textarea></label>
          </div>
          <aside class="cr-split-summary">
            <div><span>Total credit note</span><strong id="creditReturnTotal">0 UGX</strong></div>
            <div><span>Receivable reduction</span><strong id="creditReturnReceivable">0 UGX</strong></div>
            <div><span>New customer credit</span><strong id="creditReturnCustomerCredit">0 UGX</strong></div>
            <small id="creditReturnSelectionCount">No lines selected</small>
            <button class="primary full" type="submit" ${sale.items.length ? "" : "disabled"}>Complete credit return</button>
            <p>Open shift, open accounting period and administrator approval are required.</p>
          </aside>
        </div>
      </form>
    </section>`;
  }

  function returnDocumentLinks(record) {
    const documents = Array.isArray(record.documents) ? record.documents : [];
    const html = documents.find((item) => item.fileFormat === "html");
    const json = documents.find((item) => item.fileFormat === "json");
    return `<div class="cr-documents">
      ${html ? `<a href="/api/v3/finance/credit-returns/${encodeURIComponent(record.id)}/documents/${encodeURIComponent(html.id)}" target="_blank" rel="noopener">Open credit note</a>` : ""}
      ${json ? `<a href="/api/v3/finance/credit-returns/${encodeURIComponent(record.id)}/documents/${encodeURIComponent(json.id)}">Audit JSON</a>` : ""}
    </div>`;
  }

  function returnsTab() {
    const eligible = filteredEligible();
    return `<section class="cr-layout">
      <aside class="panel cr-sale-list-panel">
        <div class="cr-panel-head"><div><h2>Eligible credit invoices</h2><p>Posted credit sales with sold quantities remaining.</p></div></div>
        <label class="cr-search"><span>Find invoice</span><input id="creditReturnSearch" type="search" value="${esc(state.query)}" placeholder="Invoice, receipt or customer" autocomplete="off"></label>
        <div class="cr-sale-list">${eligible.length ? eligible.map(saleCard).join("") : '<div class="workspace-empty"><strong>No eligible credit invoices</strong><span>No matching posted credit sale has a returnable line.</span></div>'}</div>
      </aside>
      ${selectedReturnPanel()}
      <section class="panel cr-history-panel">
        <div class="cr-panel-head"><div><h2>Recent credit returns</h2><p>Permanent credit notes for the active branch.</p></div><span>${number(state.returns.length)} records</span></div>
        <div class="cr-history-list">${state.returns.length ? state.returns.map((record) => `<article class="cr-history-row">
          <div><strong>${esc(record.creditNoteNumber)}</strong><small>${esc(record.customerNumber)} · ${esc(record.customerName)} · ${esc(dateTime(record.completedAtUtc))}</small><span>${esc(record.reason)}</span></div>
          <div><strong>${money(record.returnAmountMinor)}</strong><small>${money(record.receivableReductionMinor)} receivable · ${money(record.customerCreditMinor)} customer credit</small>${returnDocumentLinks(record)}</div>
        </article>`).join("") : '<div class="workspace-empty"><strong>No credit returns yet</strong><span>Completed credit-sale returns will appear here.</span></div>'}</div>
      </section>
    </section>`;
  }

  function creditStatus(value) {
    return String(value || "open").replaceAll("_", " ");
  }

  function creditsTab() {
    const available = state.credits.filter((credit) => Number(credit.availableAmountMinor) > 0);
    return `<section class="cr-credit-grid">
      <section class="panel cr-credit-list-panel">
        <div class="cr-panel-head"><div><h2>Customer credit balances</h2><p>Liabilities created when a credit note exceeds the open invoice balance.</p></div><span>${money(available.reduce((sum, item) => sum + Number(item.availableAmountMinor || 0), 0))} available</span></div>
        <div class="cr-credit-list">${state.credits.length ? state.credits.map((credit) => `<article class="cr-credit-card">
          <div><strong>${esc(credit.creditNoteNumber)}</strong><small>${esc(credit.customerNumber)} · ${esc(credit.customerName)}</small><span>${esc(dateTime(credit.createdAtUtc))}</span></div>
          <div><strong>${money(credit.availableAmountMinor)}</strong><small>${money(credit.appliedAmountMinor)} applied of ${money(credit.originalAmountMinor)}</small><em>${esc(creditStatus(credit.status))}</em></div>
        </article>`).join("") : '<div class="workspace-empty"><strong>No customer credits</strong><span>Excess credit-note value will appear here.</span></div>'}</div>
      </section>
      <section class="panel cr-application-panel">
        <div class="cr-panel-head"><div><h2>Apply customer credit</h2><p>Settle an open receivable without moving cash.</p></div></div>
        ${available.length ? `<form id="customerCreditApplicationForm" class="cr-application-form">
          <label><span>Customer credit</span><select id="customerCreditSelect" name="creditId" required><option value="">Select available credit</option>${available.map((credit) => `<option value="${esc(credit.id)}">${esc(credit.creditNoteNumber)} · ${esc(credit.customerName)} · ${money(credit.availableAmountMinor)}</option>`).join("")}</select></label>
          <label><span>Open receivable</span><select id="customerCreditReceivable" name="receivableItemId" required disabled><option value="">Select customer credit first</option></select></label>
          <label><span>Application date</span><input type="date" name="applicationDate" value="${esc(today())}" required></label>
          <label><span>Amount</span><input id="customerCreditAmount" type="number" name="amountMinor" min="1" step="1" required></label>
          <label><span>Notes</span><textarea name="notes" rows="3" maxlength="500" placeholder="Optional customer or approval details"></textarea></label>
          <div id="customerCreditApplicationHint" class="cr-application-hint">Choose a credit to see matching open receivables.</div>
          <button class="primary full" type="submit">Apply credit to receivable</button>
        </form>` : '<div class="workspace-empty"><strong>No available credit</strong><span>Create a credit return with excess value before applying customer credit.</span></div>'}
      </section>
    </section>`;
  }

  function applicationsTab() {
    return `<section class="panel cr-history-panel">
      <div class="cr-panel-head"><div><h2>Customer-credit applications</h2><p>Immutable non-cash settlement history for the active branch.</p></div><span>${money(state.applications.reduce((sum, item) => sum + Number(item.amountMinor || 0), 0))} applied</span></div>
      <div class="cr-history-list">${state.applications.length ? state.applications.map((item) => `<article class="cr-history-row">
        <div><strong>${esc(item.applicationNumber)}</strong><small>${esc(item.customerNumber)} · ${esc(item.customerName)} · ${esc(item.applicationDate)}</small><span>${esc(item.creditNoteNumber)} applied to ${esc(item.receivableDocumentNumber)}</span></div>
        <div><strong>${money(item.amountMinor)}</strong><small>${esc(item.createdByDisplayName)} · ${esc(dateTime(item.createdAtUtc))}</small></div>
      </article>`).join("") : '<div class="workspace-empty"><strong>No applications yet</strong><span>Applied customer credits will appear here.</span></div>'}</div>
    </section>`;
  }

  function render() {
    const page = document.querySelector("#page");
    if (!page) return;
    const returnable = state.eligible.reduce((sum, sale) => sum + Number(sale.remainingReturnAmountMinor || 0), 0);
    const availableCredit = state.credits.reduce((sum, credit) => sum + Number(credit.availableAmountMinor || 0), 0);
    page.dataset.creditReturnsWorkspace = "1";
    page.innerHTML = `<div class="credit-returns-workspace">
      <section class="cr-hero"><div><span class="workspace-eyebrow">CREDIT CONTROL</span><h2>Credit returns and customer credits</h2><p>Reduce unpaid invoices, create customer-credit liabilities for excess value and apply available credit to another receivable without moving cash.</p></div><button type="button" data-page="finance">Open finance workspace</button></section>
      <section class="cr-metrics" aria-label="Credit return metrics">
        <article><span>Eligible credit invoices</span><strong>${number(state.eligible.length)}</strong><small>${money(returnable)} returnable</small></article>
        <article><span>Credit notes</span><strong>${number(state.returns.length)}</strong><small>${money(state.returns.reduce((sum, item) => sum + Number(item.returnAmountMinor || 0), 0))} returned</small></article>
        <article><span>Available customer credit</span><strong>${money(availableCredit)}</strong><small>Recorded as a liability</small></article>
        <article><span>Accounting mode</span><strong>Non-cash</strong><small>Revenue, AR, customer credits, stock and COGS</small></article>
      </section>
      <div class="cr-tabs" role="tablist" aria-label="Credit control workspace">
        ${tabButton("returns", "Credit returns", state.eligible.length)}
        ${tabButton("credits", "Customer credits", state.credits.length)}
        ${tabButton("applications", "Applications", state.applications.length)}
      </div>
      <div class="cr-tab-panel" role="tabpanel">${state.tab === "returns" ? returnsTab() : state.tab === "credits" ? creditsTab() : applicationsTab()}</div>
    </div>`;
    updateReturnSummary();
  }

  async function load() {
    if (state.loading) return;
    state.loading = true;
    const page = document.querySelector("#page");
    if (page) page.innerHTML = '<div class="page-loading"><div class="skeleton"></div><div class="skeleton" style="min-height:440px"></div></div>';
    const [eligible, history, credits, applications, receivables] = await Promise.all([
      safe("/api/v3/finance/credit-returns/eligible?limit=100"),
      safe("/api/v3/finance/credit-returns?limit=100"),
      safe("/api/v3/finance/customer-credits?limit=300"),
      safe("/api/v3/finance/customer-credit-applications?limit=300"),
      safe("/api/v3/finance/receivables?limit=500")
    ]);
    state.eligible = Array.isArray(eligible.data?.sales) ? eligible.data.sales : [];
    state.returns = Array.isArray(history.data?.returns) ? history.data.returns : [];
    state.credits = Array.isArray(credits.data?.credits) ? credits.data.credits : [];
    state.applications = Array.isArray(applications.data?.applications) ? applications.data.applications : [];
    state.receivables = Array.isArray(receivables.data?.receivables) ? receivables.data.receivables : [];
    for (const result of [eligible, history, credits, applications, receivables]) {
      if (!result.ok) notify(result.error?.message || "Part of the credit-control workspace could not be loaded.", true);
    }
    state.loading = false;
    render();
  }

  async function selectSale(saleId) {
    const result = await safe(`/api/v3/finance/credit-returns/sales/${encodeURIComponent(saleId)}`);
    if (!result.ok) {
      notify(result.error?.message || "The credit invoice could not be prepared for return.", true);
      return;
    }
    state.selected = result.data;
    render();
  }

  function updateCreditApplicationOptions() {
    const creditId = document.querySelector("#customerCreditSelect")?.value || "";
    const credit = state.credits.find((item) => item.id === creditId);
    const select = document.querySelector("#customerCreditReceivable");
    const amount = document.querySelector("#customerCreditAmount");
    const hint = document.querySelector("#customerCreditApplicationHint");
    if (!select || !amount || !hint) return;
    if (!credit) {
      select.innerHTML = '<option value="">Select customer credit first</option>';
      select.disabled = true;
      amount.value = "";
      amount.removeAttribute("max");
      hint.textContent = "Choose a credit to see matching open receivables.";
      return;
    }
    const matches = state.receivables.filter((item) =>
      item.customerId === credit.customerId && Number(item.outstandingAmountMinor) > 0);
    select.disabled = false;
    select.innerHTML = `<option value="">Select open receivable</option>${matches.map((item) => `<option value="${esc(item.id)}" data-outstanding="${esc(item.outstandingAmountMinor)}">${esc(item.documentNumber)} · ${money(item.outstandingAmountMinor)}</option>`).join("")}`;
    amount.max = String(credit.availableAmountMinor);
    amount.value = String(credit.availableAmountMinor);
    hint.textContent = matches.length
      ? `${money(credit.availableAmountMinor)} available for ${credit.customerName}.`
      : "This customer has no open receivable in the active branch.";
  }

  function constrainCreditApplicationAmount() {
    const creditId = document.querySelector("#customerCreditSelect")?.value || "";
    const credit = state.credits.find((item) => item.id === creditId);
    const selected = document.querySelector("#customerCreditReceivable")?.selectedOptions?.[0];
    const outstanding = Number(selected?.dataset.outstanding || 0);
    const amount = document.querySelector("#customerCreditAmount");
    if (!credit || !amount || !outstanding) return;
    const maximum = Math.min(Number(credit.availableAmountMinor), outstanding);
    amount.max = String(maximum);
    amount.value = String(maximum);
    const hint = document.querySelector("#customerCreditApplicationHint");
    if (hint) hint.textContent = `Maximum application: ${money(maximum)}.`;
  }

  document.addEventListener("click", (event) => {
    const tab = event.target.closest("[data-credit-tab]");
    if (tab) {
      state.tab = tab.dataset.creditTab;
      render();
      return;
    }
    const sale = event.target.closest("[data-credit-sale]");
    if (sale) selectSale(sale.dataset.creditSale);
  });

  document.addEventListener("input", (event) => {
    if (event.target.id === "creditReturnSearch") {
      state.query = event.target.value;
      render();
      document.querySelector("#creditReturnSearch")?.focus();
    }
    if (event.target.matches("[data-credit-return-quantity]")) updateReturnSummary();
  });

  document.addEventListener("change", (event) => {
    if (event.target.matches("[data-credit-return-check]")) {
      const id = event.target.dataset.creditReturnCheck;
      document.querySelector(`[data-credit-return-quantity="${CSS.escape(id)}"]`)?.toggleAttribute("disabled", !event.target.checked);
      document.querySelector(`[data-credit-return-disposition="${CSS.escape(id)}"]`)?.toggleAttribute("disabled", !event.target.checked);
      updateReturnSummary();
    }
    if (event.target.matches("[data-credit-return-disposition]")) updateReturnSummary();
    if (event.target.id === "customerCreditSelect") updateCreditApplicationOptions();
    if (event.target.id === "customerCreditReceivable") constrainCreditApplicationAmount();
  });

  document.addEventListener("submit", async (event) => {
    if (event.target.id === "creditReturnForm") {
      event.preventDefault();
      if (!state.selected) return;
      const items = selectedRequestLines();
      if (!items.length || items.some((item) => item.quantity <= 0)) {
        notify("Select at least one line and enter a valid return quantity.", true);
        return;
      }
      const values = Object.fromEntries(new FormData(event.target));
      const submit = event.target.querySelector('button[type="submit"]');
      submit.disabled = true;
      submit.textContent = "Posting credit return…";
      const result = await safe(`/api/v3/finance/credit-returns/sales/${encodeURIComponent(state.selected.saleId)}`, {
        method: "POST",
        body: JSON.stringify({ items, reason: values.reason, notes: values.notes })
      });
      if (!result.ok) {
        submit.disabled = false;
        submit.textContent = "Complete credit return";
        notify(result.error?.message || "The credit return could not be completed.", true);
        return;
      }
      notify(`Credit note ${result.data.creditNoteNumber} posted for ${money(result.data.returnAmountMinor)}.`);
      state.selected = null;
      await load();
      return;
    }

    if (event.target.id === "customerCreditApplicationForm") {
      event.preventDefault();
      const values = Object.fromEntries(new FormData(event.target));
      const submit = event.target.querySelector('button[type="submit"]');
      submit.disabled = true;
      submit.textContent = "Applying customer credit…";
      const result = await safe("/api/v3/finance/customer-credit-applications", {
        method: "POST",
        body: JSON.stringify({
          creditId: values.creditId,
          receivableItemId: values.receivableItemId,
          applicationDate: values.applicationDate,
          amountMinor: Number(values.amountMinor),
          notes: values.notes
        })
      });
      if (!result.ok) {
        submit.disabled = false;
        submit.textContent = "Apply credit to receivable";
        notify(result.error?.message || "The customer credit could not be applied.", true);
        return;
      }
      notify(`${result.data.applicationNumber} applied ${money(result.data.amountMinor)} to ${result.data.receivableDocumentNumber}.`);
      await load();
      state.tab = "applications";
      render();
    }
  });

  window.NexusCreditReturns = {
    render: load,
    isRendering: () => state.loading
  };
})();
