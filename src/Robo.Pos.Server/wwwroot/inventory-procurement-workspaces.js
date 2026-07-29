"use strict";

(function installInventoryProcurementWorkspaces() {
  const originalRenderInventory = window.renderInventory;
  if (!originalRenderInventory) return;

  const workspace = {
    inventoryFilter: "all",
    inventoryQuery: "",
    procurementTab: "reorder"
  };

  function number(value, digits = 0) {
    return Number(value || 0).toLocaleString("en-UG", { maximumFractionDigits: digits });
  }

  function moneyValue(value) {
    return `${Number(value || 0).toLocaleString("en-UG")} UGX`;
  }

  function status(value) {
    const text = String(value || "unknown").replaceAll("_", " ");
    return `<span class="ip-status ip-status-${escapeHtml(String(value || "unknown").toLowerCase())}">${escapeHtml(text)}</span>`;
  }

  function availableUnits(product) {
    const available = Number(product.availableBaseUnits ?? product.quantityBaseUnits ?? product.stockQuantity ?? 0);
    if (product.productType === "short_glass" && Number(product.glassSizeMl) > 0) {
      return {
        value: Math.floor(available / Number(product.glassSizeMl)),
        label: "glasses",
        detail: `${number(available)} ml liquid`
      };
    }
    return {
      value: available,
      label: product.stockUnit || "units",
      detail: product.saleUnit ? `Sold by ${product.saleUnit}` : "Base stock"
    };
  }

  function inventoryRows() {
    const query = workspace.inventoryQuery.trim().toLowerCase();
    return (state.inventory || []).filter((product) => {
      const stock = availableUnits(product);
      const low = Boolean(product.isLowStock) || stock.value <= Number(product.lowStockThreshold || 0);
      const typeMatch = workspace.inventoryFilter === "all"
        || (workspace.inventoryFilter === "low" && low)
        || product.productType === workspace.inventoryFilter;
      const searchMatch = !query || `${product.name} ${product.sku} ${product.barcode || ""}`.toLowerCase().includes(query);
      return typeMatch && searchMatch;
    });
  }

  function drawInventoryWorkspace() {
    const host = document.querySelector("#inventoryWorkspaceRows");
    if (!host) return;
    const rows = inventoryRows();
    host.innerHTML = rows.length ? rows.map((product) => {
      const stock = availableUnits(product);
      const low = Boolean(product.isLowStock) || stock.value <= Number(product.lowStockThreshold || 0);
      return `
        <article class="ip-stock-card ${low ? "is-low" : ""}">
          <div class="ip-stock-main">
            <div class="ip-stock-title">
              <span class="ip-product-mark">${escapeHtml(product.productType === "short_glass" ? "SG" : "ST")}</span>
              <div>
                <h3>${escapeHtml(product.name)}</h3>
                <p>${escapeHtml(product.sku)} · ${escapeHtml((product.productType || "standard").replaceAll("_", " "))}</p>
              </div>
            </div>
            ${low ? '<span class="ip-alert">Low stock</span>' : '<span class="ip-ready">Available</span>'}
          </div>
          <div class="ip-stock-metrics">
            <div><span>Available</span><strong>${number(stock.value)} ${escapeHtml(stock.label)}</strong><small>${escapeHtml(stock.detail)}</small></div>
            <div><span>Selling price</span><strong>${moneyValue(product.sellingPriceMinor)}</strong><small>Per ${escapeHtml(product.saleUnit || (product.productType === "short_glass" ? "glass" : "unit"))}</small></div>
            <div><span>Reorder level</span><strong>${number(product.reorderLevelBaseUnits || product.lowStockThreshold || 0)}</strong><small>Base units</small></div>
          </div>
          <div class="ip-stock-actions">
            <button type="button" data-stock-product="${escapeHtml(product.id)}">Adjust stock</button>
            <button type="button" data-price-product="${escapeHtml(product.id)}">Update price</button>
            <button type="button" data-page="procurement">Replenish</button>
          </div>
        </article>`;
    }).join("") : '<div class="workspace-empty"><strong>No matching stock</strong><span>Change the filter or search phrase.</span></div>';

    const count = document.querySelector("#inventoryWorkspaceCount");
    if (count) count.textContent = `${rows.length} product${rows.length === 1 ? "" : "s"}`;
  }

  window.renderInventory = async function renderInventoryWorkspace() {
    await originalRenderInventory();
    const page = document.querySelector("#page");
    if (!page) return;

    const products = state.inventory || [];
    const lowCount = products.filter((item) => {
      const stock = availableUnits(item);
      return Boolean(item.isLowStock) || stock.value <= Number(item.lowStockThreshold || 0);
    }).length;
    const shortGlassCount = products.filter((item) => item.productType === "short_glass").length;

    page.classList.add("transactional-page", "inventory-command-page");
    page.innerHTML = `
      <div class="ip-workspace">
        <section class="ip-hero">
          <div>
            <span class="workspace-eyebrow">BRANCH STOCK CONTROL</span>
            <h2>Inventory movement centre</h2>
            <p>Review real branch balances, measured short-glass quantities and replenishment needs before making controlled stock changes.</p>
          </div>
          <button class="primary" type="button" data-page="procurement">Open procurement workspace</button>
        </section>
        <section class="ip-metrics" aria-label="Inventory metrics">
          <article><span>Visible products</span><strong>${number(products.length)}</strong><small>Active branch catalogue</small></article>
          <article><span>Low stock</span><strong>${number(lowCount)}</strong><small>Needs replenishment review</small></article>
          <article><span>Short glass</span><strong>${number(shortGlassCount)}</strong><small>Measured liquid products</small></article>
        </section>
        <section class="panel ip-stock-panel">
          <div class="ip-toolbar">
            <div><h2>Branch stock cards</h2><p>Search, filter and move directly into authorised stock actions.</p></div>
            <span id="inventoryWorkspaceCount">0 products</span>
          </div>
          <div class="ip-controls">
            <label><span>Search stock</span><input id="inventoryWorkspaceSearch" type="search" placeholder="Name, SKU or barcode" autocomplete="off"></label>
            <label><span>Stock view</span><select id="inventoryWorkspaceFilter"><option value="all">All products</option><option value="low">Low stock only</option><option value="standard">Standard products</option><option value="short_glass">Short-glass products</option></select></label>
          </div>
          <div id="inventoryWorkspaceRows" class="ip-stock-list"></div>
        </section>
      </div>`;

    drawInventoryWorkspace();
  };

  function normaliseList(result, keys) {
    for (const key of keys) {
      if (Array.isArray(result?.[key])) return result[key];
    }
    return Array.isArray(result) ? result : [];
  }

  async function loadProcurementData() {
    const requests = [
      api("/api/v3/procurement/reorder-recommendations").catch(() => ({})),
      api("/api/v3/procurement/purchase-orders?limit=50").catch(() => ({})),
      api("/api/v3/procurement/goods-receipts?limit=50").catch(() => ({}))
    ];
    const [reorder, orders, receipts] = await Promise.all(requests);
    return {
      reorder: normaliseList(reorder, ["recommendations", "items", "products"]),
      orders: normaliseList(orders, ["purchaseOrders", "orders"]),
      receipts: normaliseList(receipts, ["goodsReceipts", "receipts"])
    };
  }

  function procurementRows(data) {
    if (workspace.procurementTab === "orders") {
      return data.orders.map((item) => ({
        title: item.purchaseOrderNumber || item.id,
        note: `${item.supplierName || "Supplier"} · ${item.orderDate || "Date not set"}`,
        value: moneyValue(item.totalMinor),
        status: item.status || "draft"
      }));
    }
    if (workspace.procurementTab === "receipts") {
      return data.receipts.map((item) => ({
        title: item.goodsReceiptNumber || item.receiptNumber || item.id,
        note: `${item.supplierName || "Supplier"} · ${item.receivedAtUtc || item.receiptDate || "Date not set"}`,
        value: moneyValue(item.totalMinor),
        status: item.status || "received"
      }));
    }
    return data.reorder.map((item) => ({
      title: item.productName || item.sku || item.productId,
      note: `${number(item.availableBaseUnits || item.currentQuantityBaseUnits)} available · ${number(item.suggestedQuantityBaseUnits || item.recommendedQuantityBaseUnits)} suggested`,
      value: `${number(item.suggestedQuantityBaseUnits || item.recommendedQuantityBaseUnits)} units`,
      status: item.urgency || (item.isBelowReorderLevel ? "urgent" : "recommended")
    }));
  }

  async function renderProcurementWorkspace() {
    const page = document.querySelector("#page");
    if (!page) return;
    page.innerHTML = '<div class="page-loading" aria-live="polite"><div class="skeleton"></div><div class="skeleton" style="min-height:320px"></div></div>';
    const data = await loadProcurementData();
    const rows = procurementRows(data);
    page.classList.add("transactional-page", "procurement-command-page");
    page.innerHTML = `
      <div class="ip-workspace">
        <section class="ip-hero procurement">
          <div><span class="workspace-eyebrow">CONTROLLED SUPPLY CHAIN</span><h2>Procurement workspace</h2><p>Move from reorder need to approved order and verified receipt without bypassing existing controls.</p></div>
          <button class="primary" type="button" data-page="purchases">Record direct purchase</button>
        </section>
        <section class="ip-metrics" aria-label="Procurement metrics">
          <article><span>Reorder recommendations</span><strong>${number(data.reorder.length)}</strong><small>Branch replenishment signals</small></article>
          <article><span>Purchase orders</span><strong>${number(data.orders.length)}</strong><small>Draft through completed</small></article>
          <article><span>Goods receipts</span><strong>${number(data.receipts.length)}</strong><small>Verified receiving records</small></article>
        </section>
        <section class="panel ip-procurement-panel">
          <div class="ip-toolbar"><div><h2>Supply-chain queue</h2><p>Review each controlled stage and continue in the appropriate operational screen.</p></div><button type="button" data-page="suppliers">Supplier directory</button></div>
          <div class="ip-tabs" role="tablist" aria-label="Procurement views">
            <button type="button" role="tab" data-procurement-tab="reorder" aria-selected="${workspace.procurementTab === "reorder"}">Reorder</button>
            <button type="button" role="tab" data-procurement-tab="orders" aria-selected="${workspace.procurementTab === "orders"}">Purchase orders</button>
            <button type="button" role="tab" data-procurement-tab="receipts" aria-selected="${workspace.procurementTab === "receipts"}">Goods receipts</button>
          </div>
          <div class="ip-queue">
            ${rows.length ? rows.map((item) => `<article class="ip-queue-row"><div><strong>${escapeHtml(item.title)}</strong><small>${escapeHtml(item.note)}</small></div><div><strong>${escapeHtml(item.value)}</strong>${status(item.status)}</div></article>`).join("") : '<div class="workspace-empty"><strong>No records in this view</strong><span>The current branch has no matching procurement items.</span></div>'}
          </div>
          <div class="ip-workflow-guide"><div><span>1</span><strong>Review need</strong><small>Confirm quantity and branch.</small></div><div><span>2</span><strong>Approve order</strong><small>Keep maker-checker controls.</small></div><div><span>3</span><strong>Receive stock</strong><small>Record batch and expiry.</small></div><div><span>4</span><strong>Reconcile</strong><small>Verify stock and accounting.</small></div></div>
        </section>
      </div>`;
  }

  function maybeReplaceProcurement() {
    if (location.hash !== "#procurement") return;
    const page = document.querySelector("#page");
    if (!page || page.dataset.procurementWorkspace === "1") return;
    page.dataset.procurementWorkspace = "1";
    renderProcurementWorkspace().catch(handleError);
  }

  document.addEventListener("input", (event) => {
    if (event.target.id === "inventoryWorkspaceSearch") {
      workspace.inventoryQuery = event.target.value;
      drawInventoryWorkspace();
    }
  });

  document.addEventListener("change", (event) => {
    if (event.target.id === "inventoryWorkspaceFilter") {
      workspace.inventoryFilter = event.target.value;
      drawInventoryWorkspace();
    }
  });

  document.addEventListener("click", (event) => {
    const tab = event.target.closest("[data-procurement-tab]");
    if (!tab) return;
    workspace.procurementTab = tab.dataset.procurementTab;
    renderProcurementWorkspace().catch(handleError);
  });

  window.addEventListener("hashchange", () => setTimeout(maybeReplaceProcurement, 0));
  const observer = new MutationObserver(() => maybeReplaceProcurement());
  observer.observe(document.documentElement, { childList: true, subtree: true });
})();
