"use strict";

const state = {
  user: null,
  products: [],
  inventory: [],
  categories: [],
  receipts: [],
  users: [],
  suppliers: [],
  purchases: [],
  expenses: [],
  businessReport: null,
  shift: null,
  cart: new Map()
};

const $ = (selector) => document.querySelector(selector);

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function money(value) {
  return `${Number(value || 0).toLocaleString("en-UG")} UGX`;
}

function showMessage(text, error = false) {
  const message = $("#message");

  message.textContent = text;
  message.classList.toggle("error", error);
  message.classList.remove("hidden");

  clearTimeout(showMessage.timer);

  showMessage.timer = setTimeout(() => {
    message.classList.add("hidden");
  }, 6000);
}

async function api(path, options = {}) {
  const request = {
    credentials: "same-origin",
    headers: {
      Accept: "application/json",
      ...(options.body
        ? { "Content-Type": "application/json" }
        : {}),
      ...(options.headers || {})
    },
    ...options
  };

  const response = await fetch(path, request);
  const contentType = response.headers.get("content-type") || "";

  let body = null;

  if (contentType.includes("application/json")) {
    body = await response.json();
  } else if (response.status !== 204) {
    body = await response.text();
  }

  if (!response.ok) {
    const error = new Error(
      body?.message ||
      `Request failed with HTTP ${response.status}`
    );

    error.status = response.status;
    error.body = body;

    throw error;
  }

  return body;
}

function showLogin(message = "") {
  state.user = null;
  state.cart.clear();

  $("#application").classList.add("hidden");
  $("#passwordView").classList.add("hidden");
  $("#loginView").classList.remove("hidden");

  $("#loginError").textContent = message;
  $("#loginPassword").value = "";
}

function showPasswordChange() {
  $("#loginView").classList.add("hidden");
  $("#application").classList.add("hidden");
  $("#passwordView").classList.remove("hidden");
}

function enterApplication(user) {
  state.user = user;

  $("#userName").textContent = user.displayName;
  $("#userRole").textContent = user.role;

  $("#loginView").classList.add("hidden");
  $("#passwordView").classList.add("hidden");
  $("#application").classList.remove("hidden");

  renderNavigation();

  openPage(
    user.role === "admin"
      ? "dashboard"
      : "sales"
  );
}

function renderNavigation() {
  const pages =
    state.user.role === "admin"
      ? [
          ["dashboard", "Dashboard"],
          ["inventory", "Inventory"],
          ["suppliers", "Suppliers"],
          ["purchases", "Purchases"],
          ["expenses", "Expenses"],
          ["reports", "Reports"],
          ["settings", "Settings & Backup"],
          ["sales", "Sales"],
          ["receipts", "Receipts"],
          ["users", "Teller Accounts"]
        ]
      : [
          ["sales", "Sales"],
          ["receipts", "My Receipts"]
        ];

  $("#navigation").innerHTML = pages
    .map(
      ([page, label]) => `
        <button
          class="nav-button"
          data-page="${page}"
          type="button"
        >
          ${escapeHtml(label)}
        </button>
      `
    )
    .join("");
}

async function openPage(pageName) {
  document
    .querySelectorAll(".nav-button")
    .forEach((button) => {
      button.classList.toggle(
        "active",
        button.dataset.page === pageName
      );
    });

  const pages = {
    dashboard: ["Dashboard", "Business overview"],
    inventory: ["Inventory", "Products, prices and stock"],
    suppliers: ["Suppliers", "Supplier contacts and status"],
    purchases: ["Purchases", "Receive stock from suppliers"],
    expenses: ["Expenses", "Record and review business costs"],
    reports: ["Reports", "Revenue, profit and teller performance"],
    settings: ["Settings & Backup", "Business identity and protected database copies"],
    sales: ["Sales", "Complete customer transactions"],
    receipts: ["Receipts & Invoices", "Saved audit documents"],
    users: ["Teller Accounts", "Account access and recovery"]
  };

  const [title, subtitle] = pages[pageName];

  $("#pageTitle").textContent = title;
  $("#pageSubtitle").textContent = subtitle;

  try {
    if (pageName === "dashboard") {
      await renderDashboard();
    }

    if (pageName === "inventory") {
      await renderInventory();
    }

    if (pageName === "suppliers") {
      await renderSuppliers();
    }

    if (pageName === "purchases") {
      await renderPurchases();
    }

    if (pageName === "expenses") {
      await renderExpenses();
    }

    if (pageName === "reports") {
      await renderReports();
    }

    if (pageName === "settings") {
      await renderSystemAdministration();
    }

    if (pageName === "sales") {
      await renderSales();
    }

    if (pageName === "receipts") {
      await renderReceipts();
    }

    if (pageName === "users") {
      await renderUsers();
    }
  } catch (error) {
    handleError(error);
  }
}

async function renderDashboard() {
  const summary = await api("/api/v3/admin/summary");

  $("#page").innerHTML = `
    <div class="metrics">
      ${metric("Active products", summary.activeProducts)}
      ${metric("Low stock", summary.lowStockProducts)}
      ${metric("Completed sales", summary.completedSales)}
      ${metric("Sales value", money(summary.totalSalesMinor))}
      ${metric("Active users", summary.activeUsers)}
      ${metric("Open shifts", summary.openShifts)}
      ${metric("Audit files", summary.savedDocuments)}
    </div>
  `;
}

function metric(label, value) {
  return `
    <article class="card metric">
      <span>${escapeHtml(label)}</span>
      <strong>${escapeHtml(value)}</strong>
    </article>
  `;
}

async function renderSales() {
  const [catalog, shiftResult] = await Promise.all([
    api("/api/v3/catalog/products"),
    api("/api/v3/shifts/current")
  ]);

  state.products = catalog.products;
  state.shift = shiftResult.shift;

  updateShiftStatus();

  $("#page").innerHTML = `
    <div class="toolbar">
      <input
        id="productSearch"
        placeholder="Search name, SKU or barcode"
      >

      <div class="actions">
        ${
          state.shift
            ? `
              <button
                id="closeShift"
                type="button"
              >
                Close shift
              </button>
            `
            : `
              <button
                id="openShift"
                class="primary"
                type="button"
              >
                Open shift
              </button>
            `
        }
      </div>
    </div>

    <div class="grid-two">
      <section>
        <div id="products" class="products"></div>
      </section>

      <aside class="panel">
        <h2>Current sale</h2>
        <div id="cart"></div>

        <div class="cart-total">
          <span>Total</span>
          <span id="cartTotal">0 UGX</span>
        </div>

        <form id="checkoutForm">
          <label>
            Payment method
            <select id="paymentMethod">
              <option value="cash">Cash</option>
              <option value="mobile_money">Mobile money</option>
              <option value="card">Card</option>
              <option value="bank">Bank</option>
            </select>
          </label>

          <label>
            Amount received
            <input
              id="amountReceived"
              type="number"
              min="0"
              value="0"
              required
            >
          </label>

          <label>
            <span>
              <input id="issueInvoice" type="checkbox">
              Create customer invoice
            </span>
          </label>

          <div id="customerFields" class="hidden">
            <label>
              Customer name
              <input id="customerName">
            </label>

            <label>
              Customer phone
              <input id="customerPhone">
            </label>

            <label>
              Customer address
              <input id="customerAddress">
            </label>
          </div>

          <button
            class="primary full"
            type="submit"
            ${state.shift ? "" : "disabled"}
          >
            Complete sale
          </button>
        </form>
      </aside>
    </div>
  `;

  drawProducts(state.products);
  drawCart();

  $("#productSearch").addEventListener(
    "input",
    (event) => {
      const query = event.target.value.toLowerCase();

      drawProducts(
        state.products.filter((product) =>
          `${product.name} ${product.sku} ${product.barcode || ""}`
            .toLowerCase()
            .includes(query)
        )
      );
    }
  );

  $("#issueInvoice").addEventListener(
    "change",
    (event) => {
      $("#customerFields").classList.toggle(
        "hidden",
        !event.target.checked
      );
    }
  );

  $("#openShift")?.addEventListener(
    "click",
    openShift
  );

  $("#closeShift")?.addEventListener(
    "click",
    closeShift
  );

  $("#checkoutForm").addEventListener(
    "submit",
    completeSale
  );
}

function drawProducts(products) {
  $("#products").innerHTML = products.length
    ? products
        .map(
          (product) => `
            <article class="product">
              <h3>${escapeHtml(product.name)}</h3>
              <small>${escapeHtml(product.sku)}</small>
              <p>
                <strong>
                  ${money(product.sellingPriceMinor)}
                </strong>
              </p>
              <small>
                Available:
                ${escapeHtml(product.availableBaseUnits)}
                ${escapeHtml(product.stockUnit)}
              </small>

              <button
                class="primary"
                data-add-product="${product.id}"
                type="button"
              >
                Add to sale
              </button>
            </article>
          `
        )
        .join("")
    : `<p>No products are available.</p>`;
}

function addProduct(productId) {
  const product = state.products.find(
    (item) => item.id === productId
  );

  if (!product) {
    return;
  }

  const existing = state.cart.get(productId);

  state.cart.set(productId, {
    product,
    quantity: existing
      ? existing.quantity + 1
      : 1
  });

  drawCart();
}

function drawCart() {
  const container = $("#cart");

  if (!container) {
    return;
  }

  const lines = [...state.cart.values()];

  container.innerHTML = lines.length
    ? lines
        .map(
          ({ product, quantity }) => `
            <div class="cart-line">
              <div>
                <strong>${escapeHtml(product.name)}</strong>
                <div>
                  ${quantity} ×
                  ${money(product.sellingPriceMinor)}
                </div>
              </div>

              <input
                data-cart-quantity="${product.id}"
                type="number"
                min="1"
                value="${quantity}"
              >

              <button
                class="danger"
                data-remove-product="${product.id}"
                type="button"
              >
                Remove
              </button>
            </div>
          `
        )
        .join("")
    : `<p>The cart is empty.</p>`;

  $("#cartTotal").textContent =
    money(calculateCartTotal());
}

function calculateCartTotal() {
  return [...state.cart.values()].reduce(
    (total, line) =>
      total +
      line.quantity *
      line.product.sellingPriceMinor,
    0
  );
}

async function openShift() {
  const openingCash = Number(
    prompt("Enter opening cash in UGX", "0")
  );

  if (!Number.isFinite(openingCash) ||
      openingCash < 0) {
    return;
  }

  state.shift = await api(
    "/api/v3/shifts/open",
    {
      method: "POST",
      body: JSON.stringify({
        openingCashMinor:
          Math.round(openingCash)
      })
    }
  );

  showMessage("Shift opened successfully.");
  await renderSales();
}

async function closeShift() {
  const countedCash = Number(
    prompt("Enter counted cash in UGX", "0")
  );

  if (!Number.isFinite(countedCash) ||
      countedCash < 0) {
    return;
  }

  const shift = await api(
    "/api/v3/shifts/close",
    {
      method: "POST",
      body: JSON.stringify({
        countedCashMinor:
          Math.round(countedCash),
        notes:
          "Shift closed through POS interface"
      })
    }
  );

  state.shift = null;
  updateShiftStatus();

  showMessage(
    `Shift closed. Variance: ${
      money(shift.cashVarianceMinor)
    }`
  );

  await renderSales();
}

function updateShiftStatus() {
  const badge = $("#shiftStatus");

  if (state.shift?.status === "open") {
    badge.textContent = "Shift open";
    badge.classList.add("open");
  } else {
    badge.textContent = "No open shift";
    badge.classList.remove("open");
  }
}

async function completeSale(event) {
  event.preventDefault();

  if (!state.cart.size) {
    showMessage(
      "Add at least one product.",
      true
    );

    return;
  }

  const paymentMethod =
    $("#paymentMethod").value;

  const total =
    calculateCartTotal();

  const amountReceived =
    paymentMethod === "cash"
      ? Number($("#amountReceived").value)
      : total;

  try {
    const result = await api(
      "/api/v3/sales",
      {
        method: "POST",
        body: JSON.stringify({
          items:
            [...state.cart.values()].map(
              ({ product, quantity }) => ({
                productId: product.id,
                quantity
              })
            ),
          paymentMethod,
          amountReceivedMinor:
            Math.round(amountReceived),
          issueInvoice:
            $("#issueInvoice").checked,
          customerName:
            $("#customerName")?.value.trim() || "",
          customerPhone:
            $("#customerPhone")?.value.trim() || "",
          customerAddress:
            $("#customerAddress")?.value.trim() || "",
          customerTaxNumber: "",
          notes: ""
        })
      }
    );

    state.cart.clear();

    showMessage(
      `Sale completed. Receipt ${
        result.receiptNumber
      }. Change: ${money(result.changeMinor)}`
    );

    await renderSales();
  } catch (error) {
    handleError(error);
  }
}

async function renderInventory() {
  const [productsResult, categoriesResult] =
    await Promise.all([
      api(
        "/api/v3/admin/inventory/products" +
        "?includeInactive=true"
      ),
      api("/api/v3/admin/inventory/categories")
    ]);

  state.inventory = productsResult.products;
  state.categories = categoriesResult.categories;

  $("#page").innerHTML = `
    <section class="panel">
      <h2>Add product</h2>

      <form id="createProductForm" class="form-grid">
        <label>
          Product name
          <input id="productName" required>
        </label>

        <label>
          SKU
          <input id="productSku" required>
        </label>

        <label>
          Barcode
          <input id="productBarcode">
        </label>

        <label>
          Category
          <select id="productCategory">
            <option value="">No category</option>
            ${state.categories
              .filter((category) => category.isActive)
              .map(
                (category) => `
                  <option value="${category.id}">
                    ${escapeHtml(category.name)}
                  </option>
                `
              )
              .join("")}
          </select>
        </label>

        <label>
          Type
          <select id="productType">
            <option value="standard">
              Standard item
            </option>
            <option value="bottle">
              Bottle
            </option>
            <option value="crate">
              Crate
            </option>
            <option value="short_glass">
              Short glass
            </option>
          </select>
        </label>

        <label>
          Cost price
          <input
            id="productCost"
            type="number"
            min="0"
            required
          >
        </label>

        <label>
          Selling price
          <input
            id="productSelling"
            type="number"
            min="0"
            required
          >
        </label>

        <label>
          Opening stock
          <input
            id="productStock"
            type="number"
            min="0"
            required
          >
        </label>

        <label>
          Low-stock level
          <input
            id="productLow"
            type="number"
            min="0"
            value="0"
          >
        </label>

        <label>
          Bottle volume in ml
          <input
            id="productBottleMl"
            type="number"
            min="1"
          >
        </label>

        <label>
          Glass size in ml
          <input
            id="productGlassMl"
            type="number"
            min="1"
          >
        </label>

        <label>
          Units per crate
          <input
            id="productCrateUnits"
            type="number"
            min="1"
          >
        </label>

        <button
          class="primary wide"
          type="submit"
        >
          Save product
        </button>
      </form>
    </section>

    <section class="panel" style="margin-top:17px">
      <div class="toolbar">
        <h2>Current inventory</h2>
        <input
          id="inventorySearch"
          placeholder="Search inventory"
        >
      </div>

      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Product</th>
              <th>SKU</th>
              <th>Stock</th>
              <th>Cost</th>
              <th>Selling</th>
              <th>Control</th>
            </tr>
          </thead>
          <tbody id="inventoryRows"></tbody>
        </table>
      </div>
    </section>
  `;

  drawInventoryRows(state.inventory);

  $("#inventorySearch").addEventListener(
    "input",
    (event) => {
      const query =
        event.target.value.toLowerCase();

      drawInventoryRows(
        state.inventory.filter((product) =>
          `${product.name} ${product.sku}`
            .toLowerCase()
            .includes(query)
        )
      );
    }
  );

  $("#createProductForm").addEventListener(
    "submit",
    createProduct
  );
}

function drawInventoryRows(products) {
  $("#inventoryRows").innerHTML =
    products
      .map(
        (product) => `
          <tr>
            <td>
              <strong>
                ${escapeHtml(product.name)}
              </strong>
              ${
                product.isLowStock
                  ? "<br><small>Low stock</small>"
                  : ""
              }
            </td>
            <td>${escapeHtml(product.sku)}</td>
            <td>
              ${escapeHtml(product.quantityBaseUnits)}
              ${escapeHtml(product.stockUnit)}
            </td>
            <td>${money(product.costPriceMinor)}</td>
            <td>${money(product.sellingPriceMinor)}</td>
            <td>
              <div class="actions">
                <button
                  data-price-product="${product.id}"
                  type="button"
                >
                  Change price
                </button>

                <button
                  data-stock-product="${product.id}"
                  type="button"
                >
                  Adjust stock
                </button>
              </div>
            </td>
          </tr>
        `
      )
      .join("");
}

async function createProduct(event) {
  event.preventDefault();

  const type = $("#productType").value;
  const shortGlass = type === "short_glass";
  const crate = type === "crate";

  try {
    await api(
      "/api/v3/admin/inventory/products",
      {
        method: "POST",
        body: JSON.stringify({
          categoryId:
            $("#productCategory").value || null,
          sku:
            $("#productSku").value.trim(),
          barcode:
            $("#productBarcode").value.trim() ||
            null,
          name:
            $("#productName").value.trim(),
          description: "",
          productType: type,
          stockUnit:
            shortGlass
              ? "ml"
              : crate
                ? "crate"
                : "unit",
          saleUnit:
            shortGlass
              ? "glass"
              : crate
                ? "crate"
                : "unit",
          bottleVolumeMl:
            Number($("#productBottleMl").value) ||
            null,
          glassSizeMl:
            Number($("#productGlassMl").value) ||
            null,
          unitsPerCrate:
            Number($("#productCrateUnits").value) ||
            null,
          costPriceMinor:
            Math.round(
              Number($("#productCost").value)
            ),
          sellingPriceMinor:
            Math.round(
              Number($("#productSelling").value)
            ),
          lowStockThreshold:
            Math.round(
              Number($("#productLow").value)
            ),
          openingStockBaseUnits:
            Math.round(
              Number($("#productStock").value)
            ),
          allowNegativeStock: false,
          trackExpiry: false
        })
      }
    );

    showMessage("Product created successfully.");
    await renderInventory();
  } catch (error) {
    handleError(error);
  }
}

async function changePrice(productId) {
  const product = state.inventory.find(
    (item) => item.id === productId
  );

  const cost = Number(
    prompt(
      "New cost price in UGX",
      product.costPriceMinor
    )
  );

  const selling = Number(
    prompt(
      "New selling price in UGX",
      product.sellingPriceMinor
    )
  );

  const reason = prompt(
    "Reason for changing the price",
    "Supplier or selling-price adjustment"
  );

  if (!Number.isFinite(cost) ||
      !Number.isFinite(selling) ||
      !reason) {
    return;
  }

  try {
    await api(
      `/api/v3/admin/inventory/products/` +
      `${product.id}/prices`,
      {
        method: "PUT",
        body: JSON.stringify({
          costPriceMinor: Math.round(cost),
          sellingPriceMinor: Math.round(selling),
          reason,
          expectedVersion: product.version
        })
      }
    );

    showMessage("Price changed and audited.");
    await renderInventory();
  } catch (error) {
    handleError(error);
  }
}

async function adjustStock(productId) {
  const product = state.inventory.find(
    (item) => item.id === productId
  );

  const delta = Number(
    prompt(
      "Quantity change. Use a negative number to reduce stock.",
      "0"
    )
  );

  const reason = prompt(
    "Reason for the stock adjustment",
    "Physical stock adjustment"
  );

  if (!Number.isFinite(delta) ||
      delta === 0 ||
      !reason) {
    return;
  }

  try {
    await api(
      `/api/v3/admin/inventory/products/` +
      `${product.id}/stock-adjustments`,
      {
        method: "POST",
        body: JSON.stringify({
          movementType: "adjustment",
          quantityDeltaBaseUnits:
            Math.round(delta),
          newQuantityBaseUnits: null,
          reason,
          expectedStockVersion:
            product.stockVersion
        })
      }
    );

    showMessage("Stock adjusted and audited.");
    await renderInventory();
  } catch (error) {
    handleError(error);
  }
}

async function renderReceipts() {
  const result =
    await api("/api/v3/receipts?limit=200");

  state.receipts = result.receipts;

  $("#page").innerHTML = `
    <div class="grid-two">
      <section class="panel">
        <h2>Saved receipts</h2>
        <div id="receiptList"></div>
      </section>

      <aside id="receiptDetail" class="panel">
        Select a receipt to view its items and files.
      </aside>
    </div>
  `;

  $("#receiptList").innerHTML =
    state.receipts.length
      ? state.receipts
          .map(
            (receipt) => `
              <div class="receipt-line">
                <div>
                  <strong>
                    ${escapeHtml(receipt.receiptNumber)}
                  </strong>
                  <div>
                    ${escapeHtml(receipt.tellerName)}
                    ·
                    ${new Date(
                      receipt.completedAtUtc
                    ).toLocaleString()}
                  </div>
                </div>

                <strong>
                  ${money(receipt.totalMinor)}
                </strong>

                <button
                  data-receipt="${receipt.saleId}"
                  type="button"
                >
                  View
                </button>
              </div>
            `
          )
          .join("")
      : "<p>No receipts have been created.</p>";
}

async function showReceipt(saleId) {
  const receipt =
    await api(`/api/v3/receipts/${saleId}`);

  $("#receiptDetail").innerHTML = `
    <h2>${escapeHtml(receipt.receiptNumber)}</h2>

    ${
      receipt.invoiceNumber
        ? `
          <p>
            Invoice:
            <strong>
              ${escapeHtml(receipt.invoiceNumber)}
            </strong>
          </p>
        `
        : ""
    }

    <p>
      Teller:
      ${escapeHtml(receipt.tellerName)}
    </p>

    <p>
      Total:
      <strong>${money(receipt.totalMinor)}</strong>
    </p>

    ${receipt.items
      .map(
        (item) => `
          <div class="receipt-line">
            <span>
              ${escapeHtml(item.productName)}
              × ${escapeHtml(item.quantity)}
            </span>
            <strong>
              ${money(item.lineTotalMinor)}
            </strong>
          </div>
        `
      )
      .join("")}

    <div class="document-links">
      ${receipt.documents
        .map(
          (document) => `
            <a
              href="/api/v3/receipts/${receipt.saleId}/documents/${document.id}"
              target="_blank"
              rel="noopener"
            >
              ${escapeHtml(document.documentType)}
              ${escapeHtml(
                document.fileFormat.toUpperCase()
              )}
            </a>
          `
        )
        .join("")}
    </div>
  `;
}

async function renderUsers() {
  const result =
    await api("/api/v3/admin/users");

  state.users = result.users;

  $("#page").innerHTML = `
    <section class="panel">
      <h2>User accounts</h2>

      ${state.users
        .map(
          (user) => `
            <div class="user-line">
              <div>
                <strong>
                  ${escapeHtml(user.displayName)}
                </strong>
                <div>
                  ${escapeHtml(user.username)}
                  · ${escapeHtml(user.role)}
                </div>
              </div>

              <span>
                ${
                  user.lockedUntilUtc
                    ? "Locked"
                    : user.mustChangePassword
                      ? "Password change required"
                      : "Active"
                }
              </span>

              ${
                user.role === "teller"
                  ? `
                    <button
                      data-reset-user="${user.id}"
                      type="button"
                    >
                      Reset password
                    </button>
                  `
                  : ""
              }
            </div>
          `
        )
        .join("")}
    </section>
  `;
}

async function resetTellerPassword(userId) {
  const user = state.users.find(
    (item) => item.id === userId
  );

  const administratorPassword =
    prompt(
      `Enter Baron's password to reset ${user.displayName}`
    );

  const reason =
    prompt(
      "Reason for reset",
      "Teller forgot password"
    );

  if (!administratorPassword ||
      !reason) {
    return;
  }

  try {
    const result = await api(
      `/api/v3/admin/users/${user.id}/reset-password`,
      {
        method: "POST",
        body: JSON.stringify({
          administratorPassword,
          reason
        })
      }
    );

    alert(
      `Temporary password for ${result.displayName}:\n\n` +
      `${result.temporaryPassword}\n\n` +
      "This password is shown only once."
    );

    showMessage(
      "Temporary password generated successfully."
    );

    await renderUsers();
  } catch (error) {
    handleError(error);
  }
}

function handleError(error) {
  if (error.status === 401) {
    showLogin(
      "Your session ended. Sign in again."
    );

    return;
  }

  if (error.body?.error ===
      "password_change_required") {
    showPasswordChange();
    return;
  }

  showMessage(
    error.message || "Operation failed.",
    true
  );
}

$("#loginForm").addEventListener(
  "submit",
  async (event) => {
    event.preventDefault();

    $("#loginError").textContent = "";

    try {
      const result = await api(
        "/api/v3/auth/login",
        {
          method: "POST",
          body: JSON.stringify({
            username:
              $("#loginUsername").value.trim(),
            password:
              $("#loginPassword").value
          })
        }
      );

      if (result.user.mustChangePassword) {
        showPasswordChange();
      } else {
        enterApplication(result.user);
      }
    } catch (error) {
      $("#loginError").textContent =
        error.message;
    }
  }
);

$("#passwordForm").addEventListener(
  "submit",
  async (event) => {
    event.preventDefault();

    const password =
      $("#newPassword").value;

    if (password !==
        $("#confirmPassword").value) {
      $("#passwordError").textContent =
        "The passwords do not match.";

      return;
    }

    try {
      await api(
        "/api/v3/auth/change-password",
        {
          method: "POST",
          body: JSON.stringify({
            currentPassword:
              $("#currentPassword").value,
            newPassword: password
          })
        }
      );

      showLogin(
        "Password changed. Sign in using the new password."
      );
    } catch (error) {
      $("#passwordError").textContent =
        error.message;
    }
  }
);

$("#logoutButton").addEventListener(
  "click",
  async () => {
    try {
      await api(
        "/api/v3/auth/logout",
        { method: "POST" }
      );
    } finally {
      showLogin();
    }
  }
);

$("#navigation").addEventListener(
  "click",
  (event) => {
    const button =
      event.target.closest("[data-page]");

    if (button) {
      openPage(button.dataset.page);
    }
  }
);

document.addEventListener(
  "click",
  (event) => {
    const add =
      event.target.closest(
        "[data-add-product]"
      );

    const remove =
      event.target.closest(
        "[data-remove-product]"
      );

    const price =
      event.target.closest(
        "[data-price-product]"
      );

    const stock =
      event.target.closest(
        "[data-stock-product]"
      );

    const receipt =
      event.target.closest(
        "[data-receipt]"
      );

    const reset =
      event.target.closest(
        "[data-reset-user]"
      );

    if (add) {
      addProduct(add.dataset.addProduct);
    }

    if (remove) {
      state.cart.delete(
        remove.dataset.removeProduct
      );

      drawCart();
    }

    if (price) {
      changePrice(
        price.dataset.priceProduct
      );
    }

    if (stock) {
      adjustStock(
        stock.dataset.stockProduct
      );
    }

    if (receipt) {
      showReceipt(
        receipt.dataset.receipt
      );
    }

    if (reset) {
      resetTellerPassword(
        reset.dataset.resetUser
      );
    }
  }
);

document.addEventListener(
  "change",
  (event) => {
    if (event.target.matches(
          "[data-cart-quantity]"
        )) {
      const productId =
        event.target.dataset.cartQuantity;

      const line =
        state.cart.get(productId);

      if (line) {
        line.quantity =
          Math.max(
            1,
            Math.round(
              Number(event.target.value)
            )
          );

        state.cart.set(
          productId,
          line
        );

        drawCart();
      }
    }

    if (event.target.id ===
        "paymentMethod" &&
        event.target.value !== "cash") {
      $("#amountReceived").value =
        calculateCartTotal();
    }
  }
);

(async function boot() {
  try {
    const result =
      await api("/api/v3/auth/me");

    const user =
      result.user || result;

    if (user.mustChangePassword) {
      showPasswordChange();
    } else {
      enterApplication(user);
    }
  } catch {
    showLogin();
  }
})();
