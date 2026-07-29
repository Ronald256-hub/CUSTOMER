"use strict";

(function stabiliseProcurementNavigation() {
  let rendering = false;
  let activeTab = "reorder";

  function number(value) {
    return Number(value || 0).toLocaleString("en-UG");
  }

  function moneyValue(value) {
    return `${number(value)} UGX`;
  }

  function list(source, keys) {
    for (const key of keys) {
      if (Array.isArray(source?.[key])) return source[key];
    }
    return Array.isArray(source) ? source : [];
  }

  async function data() {
    const [reorder, orders, receipts] = await Promise.all([
      api("/api/v3/procurement/reorder-recommendations").catch(() => ({})),
      api("/api/v3/procurement/purchase-orders?limit=50").catch(() => ({})),
      api("/api/v3/procurement/goods-receipts?limit=50").catch(() => ({}))
    ]);
    return {
      reorder: list(reorder, ["recommendations", "items", "products"]),
      orders: list(orders, ["purchaseOrders", "orders"]),
      receipts: list(receipts, ["goodsReceipts", "receipts"])
    };
  }

  function rows(source) {
    if (activeTab === "orders") {
      return source.orders.map((item) => ({
        title: item.purchaseOrderNumber || item.id,
        note: `${item.supplierName || "Supplier"} · ${item.orderDate || "Date not set"}`,
        value: moneyValue(item.totalMinor),
        state: item.status || "draft"
      }));
    }
    if (activeTab === "receipts") {
      return source.receipts.map((item) => ({
        title: item.goodsReceiptNumber || item.receiptNumber || item.id,
        note: `${item.supplierName || "Supplier"} · ${item.receiptDate || item.receivedAtUtc || "Date not set"}`,
        value: moneyValue(item.totalMinor),
        state: item.status || "received"
      }));
    }
    return source.reorder.map((item) => ({
      title: item.productName || item.sku || item.productId,
      note: `${number(item.availableBaseUnits || item.currentQuantityBaseUnits)} available · ${number(item.suggestedQuantityBaseUnits || item.recommendedQuantityBaseUnits)} suggested`,
      value: `${number(item.suggestedQuantityBaseUnits || item.recommendedQuantityBaseUnits)} units`,
      state: item.urgency || (item.isBelowReorderLevel ? "urgent" : "recommended")
    }));
  }

  async function render() {
    if (rendering || location.hash !== "#procurement") return;
    const page = document.querySelector("#page");
    if (!page) return;
    rendering = true;
    page.dataset.procurementWorkspace = "1";
    try {
      const source = await data();
      const queue = rows(source);
      page.classList.add("transactional-page", "procurement-command-page");
      page.innerHTML = `
        <div class="ip-workspace">
          <section class="ip-hero procurement">
            <div><span class="workspace-eyebrow">CONTROLLED SUPPLY CHAIN</span><h2>Procurement workspace</h2><p>Move from reorder need to approved order and verified receipt without bypassing existing controls.</p></div>
            <button class="primary" type="button" data-page="purchases">Record direct purchase</button>
          </section>
          <section class="ip-metrics" aria-label="Procurement metrics">
            <article><span>Reorder recommendations</span><strong>${number(source.reorder.length)}</strong><small>Branch replenishment signals</small></article>
            <article><span>Purchase orders</span><strong>${number(source.orders.length)}</strong><small>Draft through completed</small></article>
            <article><span>Goods receipts</span><strong>${number(source.receipts.length)}</strong><small>Verified receiving records</small></article>
          </section>
          <section class="panel ip-procurement-panel">
            <div class="ip-toolbar"><div><h2>Supply-chain queue</h2><p>Review each controlled stage and continue in the appropriate operational screen.</p></div><button type="button" data-page="suppliers">Supplier directory</button></div>
            <div class="ip-tabs" role="tablist" aria-label="Procurement views">
              <button type="button" role="tab" data-stable-procurement-tab="reorder" aria-selected="${activeTab === "reorder"}">Reorder</button>
              <button type="button" role="tab" data-stable-procurement-tab="orders" aria-selected="${activeTab === "orders"}">Purchase orders</button>
              <button type="button" role="tab" data-stable-procurement-tab="receipts" aria-selected="${activeTab === "receipts"}">Goods receipts</button>
            </div>
            <div class="ip-queue">
              ${queue.length ? queue.map((item) => `<article class="ip-queue-row"><div><strong>${escapeHtml(item.title)}</strong><small>${escapeHtml(item.note)}</small></div><div><strong>${escapeHtml(item.value)}</strong><span class="ip-status">${escapeHtml(String(item.state).replaceAll("_", " "))}</span></div></article>`).join("") : '<div class="workspace-empty"><strong>No records in this view</strong><span>The current branch has no matching procurement items.</span></div>'}
            </div>
            <div class="ip-workflow-guide"><div><span>1</span><strong>Review need</strong><small>Confirm quantity and branch.</small></div><div><span>2</span><strong>Approve order</strong><small>Keep maker-checker controls.</small></div><div><span>3</span><strong>Receive stock</strong><small>Record batch and expiry.</small></div><div><span>4</span><strong>Reconcile</strong><small>Verify stock and accounting.</small></div></div>
          </section>
        </div>`;
    } finally {
      rendering = false;
    }
  }

  function schedule() {
    setTimeout(() => {
      const page = document.querySelector("#page");
      if (location.hash !== "#procurement") {
        if (page) delete page.dataset.procurementWorkspace;
        return;
      }
      render().catch(handleError);
    }, 0);
  }

  document.addEventListener("click", (event) => {
    const tab = event.target.closest("[data-stable-procurement-tab]");
    if (!tab) return;
    activeTab = tab.dataset.stableProcurementTab;
    const page = document.querySelector("#page");
    if (page) delete page.dataset.procurementWorkspace;
    schedule();
  });

  window.addEventListener("hashchange", schedule);
  new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
})();
