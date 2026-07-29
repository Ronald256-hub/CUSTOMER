"use strict";

(function installTransactionalWorkspaces() {
  const originalRenderSales = window.renderSales;
  const originalDrawProducts = window.drawProducts;
  const originalDrawCart = window.drawCart;

  if (!originalRenderSales || !originalDrawProducts || !originalDrawCart) {
    return;
  }

  function moneyValue(value) {
    return `${Number(value || 0).toLocaleString("en-UG")} UGX`;
  }

  function productUnitLabel(product) {
    if (product.productType === "short_glass") {
      return `${product.glassSizeMl || 0} ml glass`;
    }
    return product.saleUnit || product.stockUnit || "unit";
  }

  function saleLineCount() {
    return [...state.cart.values()].reduce((total, line) => total + Number(line.quantity || 0), 0);
  }

  function updateSaleWorkspaceSummary() {
    const total = calculateCartTotal();
    const lines = saleLineCount();
    const totalHost = document.querySelector("#workspaceSaleTotal");
    const lineHost = document.querySelector("#workspaceSaleLines");
    const paymentHost = document.querySelector("#workspacePaymentSummary");

    if (totalHost) totalHost.textContent = moneyValue(total);
    if (lineHost) lineHost.textContent = `${lines} item${lines === 1 ? "" : "s"}`;
    if (paymentHost) paymentHost.textContent = document.querySelector("#paymentMethod")?.selectedOptions?.[0]?.textContent || "Cash";
  }

  window.drawProducts = function drawTransactionalProducts(products) {
    const host = document.querySelector("#products");
    if (!host) return;

    host.innerHTML = products.length
      ? products.map((product) => {
          const available = Number(product.availableBaseUnits || 0);
          const glass = product.productType === "short_glass";
          const remainingGlasses = glass && product.glassSizeMl
            ? Math.floor(available / Number(product.glassSizeMl))
            : available;
          return `
            <article class="workspace-product ${glass ? "short-glass-product" : ""}">
              <div class="workspace-product-head">
                <span class="workspace-product-type">${escapeHtml(glass ? "SHORT GLASS" : (product.productType || "PRODUCT").replaceAll("_", " ").toUpperCase())}</span>
                <span class="workspace-stock ${remainingGlasses <= 0 ? "danger" : ""}">${escapeHtml(remainingGlasses)} ${escapeHtml(glass ? "glasses" : (product.stockUnit || "units"))}</span>
              </div>
              <div class="workspace-product-body">
                <h3>${escapeHtml(product.name)}</h3>
                <small>${escapeHtml(product.sku)} · ${escapeHtml(productUnitLabel(product))}</small>
                <strong>${moneyValue(product.sellingPriceMinor)}</strong>
              </div>
              <button class="primary workspace-add" data-add-product="${product.id}" type="button" ${remainingGlasses <= 0 ? "disabled" : ""}>
                ${glass ? "Dispense one glass" : "Add to sale"}
              </button>
            </article>
          `;
        }).join("")
      : `<div class="workspace-empty">No product matches this search.</div>`;
  };

  window.drawCart = function drawTransactionalCart() {
    const host = document.querySelector("#cart");
    if (!host) return;

    const lines = [...state.cart.values()];
    host.innerHTML = lines.length
      ? lines.map(({ product, quantity }) => `
          <article class="workspace-cart-line">
            <div>
              <strong>${escapeHtml(product.name)}</strong>
              <small>${escapeHtml(product.sku)} · ${escapeHtml(productUnitLabel(product))}</small>
            </div>
            <div class="workspace-quantity-control">
              <button type="button" data-cart-step="-1" data-cart-product="${product.id}" aria-label="Reduce ${escapeHtml(product.name)}">−</button>
              <input data-cart-quantity="${product.id}" type="number" min="1" value="${quantity}" aria-label="Quantity for ${escapeHtml(product.name)}">
              <button type="button" data-cart-step="1" data-cart-product="${product.id}" aria-label="Increase ${escapeHtml(product.name)}">+</button>
            </div>
            <div class="workspace-line-value">
              <strong>${moneyValue(Number(quantity) * Number(product.sellingPriceMinor || 0))}</strong>
              <button class="text-danger" data-remove-product="${product.id}" type="button">Remove</button>
            </div>
          </article>
        `).join("")
      : `<div class="workspace-empty"><strong>No items yet</strong><span>Search or scan a product to begin the sale.</span></div>`;

    const total = document.querySelector("#cartTotal");
    if (total) total.textContent = moneyValue(calculateCartTotal());
    updateSaleWorkspaceSummary();
  };

  window.renderSales = async function renderTransactionalSales() {
    await originalRenderSales();
    const page = document.querySelector("#page");
    if (!page) return;

    const toolbar = page.querySelector(".toolbar");
    const grid = page.querySelector(".grid-two");
    if (!toolbar || !grid) return;

    toolbar.classList.add("workspace-sales-toolbar");
    grid.classList.add("transactional-sales-grid");
    page.classList.add("transactional-page");

    toolbar.insertAdjacentHTML("afterbegin", `
      <div class="workspace-sales-heading">
        <span class="workspace-eyebrow">LIVE TRANSACTION</span>
        <strong>${state.shift ? "Shift open and ready" : "Open a shift to begin selling"}</strong>
      </div>
    `);

    const productSection = grid.querySelector("section");
    productSection?.insertAdjacentHTML("afterbegin", `
      <div class="workspace-section-title">
        <div><h2>Product catalogue</h2><p>Search by name, SKU or barcode. Short-glass stock is shown as sellable glasses.</p></div>
        <span class="workspace-result-count">${state.products.length} products</span>
      </div>
    `);

    const cartPanel = grid.querySelector("aside.panel");
    cartPanel?.classList.add("workspace-checkout");
    const heading = cartPanel?.querySelector("h2");
    if (heading) {
      const replacement = document.createElement("div");
      replacement.className = "workspace-section-title compact";
      replacement.innerHTML = `<div><h2>Current sale</h2><p id="workspaceSaleLines">0 items</p></div><strong id="workspaceSaleTotal">0 UGX</strong>`;
      heading.replaceWith(replacement);
    }

    const checkoutForm = document.querySelector("#checkoutForm");
    checkoutForm?.insertAdjacentHTML("afterbegin", `
      <div class="workspace-payment-banner">
        <span>Payment method</span>
        <strong id="workspacePaymentSummary">Cash</strong>
      </div>
    `);

    const submit = checkoutForm?.querySelector('button[type="submit"]');
    if (submit) submit.textContent = "Complete sale and issue receipt";

    window.drawProducts(state.products);
    window.drawCart();
    document.querySelector("#productSearch")?.focus();
  };

  document.addEventListener("click", (event) => {
    const step = event.target.closest("[data-cart-step]");
    if (!step) return;

    const productId = step.dataset.cartProduct;
    const line = state.cart.get(productId);
    if (!line) return;

    line.quantity = Math.max(1, Number(line.quantity || 1) + Number(step.dataset.cartStep || 0));
    state.cart.set(productId, line);
    window.drawCart();
  });

  document.addEventListener("change", (event) => {
    if (event.target.id === "paymentMethod") updateSaleWorkspaceSummary();
  });
})();
