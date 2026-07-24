"use strict";

const businessUi = {
  purchaseLines: []
};

function businessToday() {
  return new Date().toISOString().slice(0, 10);
}

function businessMonthStart() {
  const now = new Date();

  return [
    now.getFullYear(),
    String(now.getMonth() + 1).padStart(2, "0"),
    "01"
  ].join("-");
}

function businessLineId() {
  return globalThis.crypto?.randomUUID?.() ||
    `line-${Date.now()}-${Math.random()}`;
}

function newPurchaseLine() {
  return {
    id: businessLineId(),
    productId: "",
    quantityBaseUnits: 1,
    unitCostMinor: 0,
    batchNumber: "",
    expiryDate: ""
  };
}

async function renderSuppliers() {
  const result = await api(
    "/api/v3/admin/suppliers?includeInactive=true"
  );

  state.suppliers = result.suppliers;

  $("#page").innerHTML = `
    <section class="panel">
      <h2>Add supplier</h2>

      <form id="supplierForm" class="form-grid">
        <label>
          Supplier name
          <input id="supplierName" maxlength="150" required>
        </label>

        <label>
          Phone
          <input id="supplierPhone" maxlength="50">
        </label>

        <label>
          Email
          <input id="supplierEmail" type="email" maxlength="150">
        </label>

        <label>
          Address
          <input id="supplierAddress" maxlength="250">
        </label>

        <label class="wide">
          Notes
          <textarea id="supplierNotes" maxlength="500"></textarea>
        </label>

        <button class="primary wide" type="submit">
          Save supplier
        </button>
      </form>
    </section>

    <section class="panel business-section">
      <div class="toolbar">
        <h2>Supplier directory</h2>

        <input
          id="supplierSearch"
          placeholder="Search suppliers"
        >
      </div>

      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Supplier</th>
              <th>Phone</th>
              <th>Address</th>
              <th>Status</th>
              <th>Control</th>
            </tr>
          </thead>

          <tbody id="supplierRows"></tbody>
        </table>
      </div>
    </section>
  `;

  drawSupplierRows(state.suppliers);

  $("#supplierForm").addEventListener(
    "submit",
    createSupplier
  );

  $("#supplierSearch").addEventListener(
    "input",
    (event) => {
      const query = event.target.value.toLowerCase();

      drawSupplierRows(
        state.suppliers.filter((supplier) =>
          `${supplier.name} ${supplier.phone} ${supplier.address}`
            .toLowerCase()
            .includes(query)
        )
      );
    }
  );
}

function drawSupplierRows(suppliers) {
  $("#supplierRows").innerHTML = suppliers.length
    ? suppliers.map((supplier) => `
        <tr>
          <td>
            <strong>${escapeHtml(supplier.name)}</strong>
            ${
              supplier.email
                ? `<br><small>${escapeHtml(supplier.email)}</small>`
                : ""
            }
          </td>

          <td>${escapeHtml(supplier.phone || "—")}</td>
          <td>${escapeHtml(supplier.address || "—")}</td>

          <td>
            <span class="business-status ${
              supplier.isActive ? "active" : "inactive"
            }">
              ${supplier.isActive ? "Active" : "Inactive"}
            </span>
          </td>

          <td>
            <button
              data-edit-supplier="${supplier.id}"
              type="button"
            >
              Edit
            </button>
          </td>
        </tr>
      `).join("")
    : `
      <tr>
        <td colspan="5">No suppliers found.</td>
      </tr>
    `;
}

async function createSupplier(event) {
  event.preventDefault();

  try {
    await api(
      "/api/v3/admin/suppliers",
      {
        method: "POST",
        body: JSON.stringify({
          name: $("#supplierName").value.trim(),
          phone: $("#supplierPhone").value.trim(),
          email: $("#supplierEmail").value.trim(),
          address: $("#supplierAddress").value.trim(),
          notes: $("#supplierNotes").value.trim()
        })
      }
    );

    showMessage("Supplier created successfully.");
    await renderSuppliers();
  } catch (error) {
    handleError(error);
  }
}

async function editSupplier(supplierId) {
  const supplier = state.suppliers.find(
    (item) => item.id === supplierId
  );

  if (!supplier) {
    return;
  }

  const name = prompt(
    "Supplier name",
    supplier.name
  );

  if (!name?.trim()) {
    return;
  }

  const phone = prompt(
    "Phone",
    supplier.phone || ""
  );

  if (phone === null) {
    return;
  }

  const address = prompt(
    "Address",
    supplier.address || ""
  );

  if (address === null) {
    return;
  }

  const statusAnswer = confirm(
    "Should this supplier remain active?"
  );

  try {
    await api(
      `/api/v3/admin/suppliers/${supplier.id}`,
      {
        method: "PUT",
        body: JSON.stringify({
          name: name.trim(),
          phone: phone.trim(),
          email: supplier.email,
          address: address.trim(),
          notes: supplier.notes,
          isActive: statusAnswer
        })
      }
    );

    showMessage("Supplier updated and audited.");
    await renderSuppliers();
  } catch (error) {
    handleError(error);
  }
}

async function renderPurchases() {
  const [supplierResult, inventoryResult, purchaseResult] =
    await Promise.all([
      api("/api/v3/admin/suppliers"),
      api(
        "/api/v3/admin/inventory/products" +
        "?includeInactive=false"
      ),
      api("/api/v3/admin/purchases?limit=200")
    ]);

  state.suppliers = supplierResult.suppliers;
  state.inventory = inventoryResult.products;
  state.purchases = purchaseResult.purchases;

  if (!businessUi.purchaseLines.length) {
    businessUi.purchaseLines = [newPurchaseLine()];
  }

  $("#page").innerHTML = `
    <section class="panel">
      <h2>Receive stock purchase</h2>

      <form id="purchaseForm">
        <div class="form-grid">
          <label>
            Supplier
            <select id="purchaseSupplier">
              <option value="">No supplier selected</option>

              ${state.suppliers.map((supplier) => `
                <option value="${supplier.id}">
                  ${escapeHtml(supplier.name)}
                </option>
              `).join("")}
            </select>
          </label>

          <label>
            Supplier invoice number
            <input
              id="supplierInvoiceNumber"
              maxlength="100"
            >
          </label>

          <label class="wide">
            Notes
            <input id="purchaseNotes" maxlength="500">
          </label>
        </div>

        <div class="purchase-heading">
          <h3>Purchase items</h3>

          <button
            id="addPurchaseLine"
            type="button"
          >
            Add another product
          </button>
        </div>

        <div id="purchaseLines"></div>

        <div class="purchase-total">
          <span>Purchase total</span>
          <strong id="purchaseTotal">0 UGX</strong>
        </div>

        <button class="primary full" type="submit">
          Receive purchase and update stock
        </button>
      </form>
    </section>

    <section class="panel business-section">
      <h2>Purchase history</h2>

      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Purchase</th>
              <th>Date</th>
              <th>Supplier</th>
              <th>Supplier invoice</th>
              <th>Received by</th>
              <th>Total</th>
            </tr>
          </thead>

          <tbody>
            ${
              state.purchases.length
                ? state.purchases.map((purchase) => `
                    <tr>
                      <td>
                        <strong>
                          ${escapeHtml(purchase.purchaseNumber)}
                        </strong>
                      </td>

                      <td>
                        ${new Date(
                          purchase.receivedAtUtc
                        ).toLocaleString()}
                      </td>

                      <td>
                        ${escapeHtml(
                          purchase.supplierName || "No supplier"
                        )}
                      </td>

                      <td>
                        ${escapeHtml(
                          purchase.supplierInvoiceNumber || "—"
                        )}
                      </td>

                      <td>${escapeHtml(purchase.receivedBy)}</td>
                      <td>${money(purchase.totalMinor)}</td>
                    </tr>
                  `).join("")
                : `
                  <tr>
                    <td colspan="6">
                      No purchases have been received.
                    </td>
                  </tr>
                `
            }
          </tbody>
        </table>
      </div>
    </section>
  `;

  drawPurchaseLines();

  $("#purchaseForm").addEventListener(
    "submit",
    receivePurchase
  );

  $("#addPurchaseLine").addEventListener(
    "click",
    () => {
      businessUi.purchaseLines.push(newPurchaseLine());
      drawPurchaseLines();
    }
  );
}

function drawPurchaseLines() {
  const productOptions = state.inventory
    .filter((product) => product.isActive)
    .map((product) => `
      <option value="${product.id}">
        ${escapeHtml(product.name)}
        — ${escapeHtml(product.sku)}
      </option>
    `)
    .join("");

  $("#purchaseLines").innerHTML =
    businessUi.purchaseLines.map((line, index) => `
      <div
        class="purchase-line"
        data-purchase-line="${line.id}"
      >
        <label>
          Product
          <select
            data-purchase-product="${line.id}"
            required
          >
            <option value="">Select product</option>

            ${state.inventory
              .filter((product) => product.isActive)
              .map((product) => `
                <option
                  value="${product.id}"
                  ${
                    product.id === line.productId
                      ? "selected"
                      : ""
                  }
                >
                  ${escapeHtml(product.name)}
                  — ${escapeHtml(product.sku)}
                </option>
              `)
              .join("")}
          </select>
        </label>

        <label>
          Quantity
          <input
            data-purchase-quantity="${line.id}"
            type="number"
            min="1"
            step="1"
            value="${line.quantityBaseUnits}"
            required
          >
        </label>

        <label>
          Unit cost (UGX)
          <input
            data-purchase-cost="${line.id}"
            type="number"
            min="0"
            step="1"
            value="${line.unitCostMinor}"
            required
          >
        </label>

        <label>
          Batch
          <input
            data-purchase-batch="${line.id}"
            value="${escapeHtml(line.batchNumber)}"
            maxlength="100"
          >
        </label>

        <label>
          Expiry date
          <input
            data-purchase-expiry="${line.id}"
            type="date"
            value="${escapeHtml(line.expiryDate)}"
          >
        </label>

        <button
          class="danger purchase-remove"
          data-remove-purchase-line="${line.id}"
          type="button"
          ${businessUi.purchaseLines.length === 1 ? "disabled" : ""}
        >
          Remove
        </button>

        <div class="purchase-line-total">
          Line ${index + 1}:
          <strong>
            ${money(
              line.quantityBaseUnits *
              line.unitCostMinor
            )}
          </strong>
        </div>
      </div>
    `).join("");

  updatePurchaseTotal();
}

function updatePurchaseLine(lineId, field, value) {
  const line = businessUi.purchaseLines.find(
    (item) => item.id === lineId
  );

  if (!line) {
    return;
  }

  line[field] = value;
  updatePurchaseTotal();
}

function updatePurchaseTotal() {
  const total = businessUi.purchaseLines.reduce(
    (sum, line) =>
      sum +
      Number(line.quantityBaseUnits || 0) *
      Number(line.unitCostMinor || 0),
    0
  );

  const output = $("#purchaseTotal");

  if (output) {
    output.textContent = money(total);
  }

  document
    .querySelectorAll("[data-purchase-line]")
    .forEach((element) => {
      const line = businessUi.purchaseLines.find(
        (item) =>
          item.id === element.dataset.purchaseLine
      );

      const outputLine = element.querySelector(
        ".purchase-line-total strong"
      );

      if (line && outputLine) {
        outputLine.textContent = money(
          Number(line.quantityBaseUnits || 0) *
          Number(line.unitCostMinor || 0)
        );
      }
    });
}

async function receivePurchase(event) {
  event.preventDefault();

  const items = businessUi.purchaseLines.map((line) => ({
    productId: line.productId,
    quantityBaseUnits:
      Math.round(Number(line.quantityBaseUnits)),
    unitCostMinor:
      Math.round(Number(line.unitCostMinor)),
    batchNumber: line.batchNumber.trim(),
    expiryDate: line.expiryDate || null
  }));

  if (items.some((item) => !item.productId)) {
    showMessage(
      "Select a product for every purchase line.",
      true
    );

    return;
  }

  try {
    const result = await api(
      "/api/v3/admin/purchases",
      {
        method: "POST",
        body: JSON.stringify({
          supplierId:
            $("#purchaseSupplier").value || null,
          supplierInvoiceNumber:
            $("#supplierInvoiceNumber").value.trim(),
          notes:
            $("#purchaseNotes").value.trim(),
          items
        })
      }
    );

    businessUi.purchaseLines = [newPurchaseLine()];

    showMessage(
      `${result.purchaseNumber} received. ` +
      `Stock increased by the recorded quantities.`
    );

    await renderPurchases();
  } catch (error) {
    handleError(error);
  }
}

async function renderExpenses() {
  const result = await api(
    "/api/v3/admin/expenses" +
    "?includeVoided=true&limit=500"
  );

  state.expenses = result.expenses;

  $("#page").innerHTML = `
    <section class="panel">
      <h2>Record business expense</h2>

      <form id="expenseForm" class="form-grid">
        <label>
          Category
          <select id="expenseCategory">
            <option>Transport</option>
            <option>Utilities</option>
            <option>Rent</option>
            <option>Wages</option>
            <option>Supplies</option>
            <option>Repairs</option>
            <option>Licences</option>
            <option>Other</option>
          </select>
        </label>

        <label>
          Amount (UGX)
          <input
            id="expenseAmount"
            type="number"
            min="1"
            step="1"
            required
          >
        </label>

        <label>
          Payment method
          <select id="expensePayment">
            <option value="cash">Cash</option>
            <option value="mobile_money">Mobile money</option>
            <option value="bank">Bank</option>
            <option value="other">Other</option>
          </select>
        </label>

        <label>
          Expense date
          <input
            id="expenseDate"
            type="date"
            value="${businessToday()}"
            required
          >
        </label>

        <label class="wide">
          Description
          <input
            id="expenseDescription"
            maxlength="250"
            required
          >
        </label>

        <button class="primary wide" type="submit">
          Save expense
        </button>
      </form>
    </section>

    <section class="panel business-section">
      <h2>Expense history</h2>

      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Expense</th>
              <th>Date</th>
              <th>Category</th>
              <th>Description</th>
              <th>Payment</th>
              <th>Amount</th>
              <th>Status</th>
              <th>Control</th>
            </tr>
          </thead>

          <tbody>
            ${
              state.expenses.length
                ? state.expenses.map((expense) => `
                    <tr>
                      <td>
                        <strong>
                          ${escapeHtml(expense.expenseNumber)}
                        </strong>
                      </td>

                      <td>${escapeHtml(expense.expenseDate)}</td>
                      <td>${escapeHtml(expense.category)}</td>
                      <td>${escapeHtml(expense.description)}</td>
                      <td>${escapeHtml(expense.paymentMethod)}</td>
                      <td>${money(expense.amountMinor)}</td>

                      <td>
                        <span class="business-status ${
                          expense.isVoided
                            ? "inactive"
                            : "active"
                        }">
                          ${
                            expense.isVoided
                              ? "Voided"
                              : "Recorded"
                          }
                        </span>
                      </td>

                      <td>
                        ${
                          expense.isVoided
                            ? escapeHtml(
                                expense.voidReason || "Voided"
                              )
                            : `
                              <button
                                class="danger"
                                data-void-expense="${expense.id}"
                                type="button"
                              >
                                Void
                              </button>
                            `
                        }
                      </td>
                    </tr>
                  `).join("")
                : `
                  <tr>
                    <td colspan="8">
                      No expenses have been recorded.
                    </td>
                  </tr>
                `
            }
          </tbody>
        </table>
      </div>
    </section>
  `;

  $("#expenseForm").addEventListener(
    "submit",
    createExpense
  );
}

async function createExpense(event) {
  event.preventDefault();

  try {
    const result = await api(
      "/api/v3/admin/expenses",
      {
        method: "POST",
        body: JSON.stringify({
          category: $("#expenseCategory").value,
          description:
            $("#expenseDescription").value.trim(),
          amountMinor:
            Math.round(Number($("#expenseAmount").value)),
          paymentMethod:
            $("#expensePayment").value,
          expenseDate:
            $("#expenseDate").value
        })
      }
    );

    showMessage(
      `${result.expenseNumber} recorded successfully.`
    );

    await renderExpenses();
  } catch (error) {
    handleError(error);
  }
}

async function voidExpense(expenseId) {
  const reason = prompt(
    "Enter the reason for voiding this expense"
  );

  if (!reason?.trim()) {
    return;
  }

  try {
    await api(
      `/api/v3/admin/expenses/${expenseId}/void`,
      {
        method: "POST",
        body: JSON.stringify({
          reason: reason.trim()
        })
      }
    );

    showMessage(
      "Expense voided. It remains in the audit history."
    );

    await renderExpenses();
  } catch (error) {
    handleError(error);
  }
}

async function renderReports(
  from = businessMonthStart(),
  to = businessToday()
) {
  const query = new URLSearchParams({ from, to });

  const report = await api(
    `/api/v3/admin/reports/summary?${query}`
  );

  state.businessReport = report;

  $("#page").innerHTML = `
    <section class="panel">
      <form id="reportForm" class="toolbar">
        <label>
          From
          <input
            id="reportFrom"
            type="date"
            value="${escapeHtml(report.from)}"
            required
          >
        </label>

        <label>
          To
          <input
            id="reportTo"
            type="date"
            value="${escapeHtml(report.to)}"
            required
          >
        </label>

        <div class="actions">
          <button class="primary" type="submit">
            Run report
          </button>

          <a
            class="business-link-button"
            href="/api/v3/admin/reports/sales.csv?${query}"
          >
            Download sales CSV
          </a>
        </div>
      </form>
    </section>

    <div class="metrics business-section">
      ${metric("Revenue", money(report.revenueMinor))}
      ${metric(
        "Cost of goods",
        money(report.costOfGoodsMinor)
      )}
      ${metric(
        "Gross profit",
        money(report.grossProfitMinor)
      )}
      ${metric(
        "Expenses",
        money(report.expenseTotalMinor)
      )}
      ${metric(
        "Net profit",
        money(report.netProfitMinor)
      )}
      ${metric(
        "Stock purchases",
        money(report.purchaseTotalMinor)
      )}
      ${metric("Sales", report.salesCount)}
    </div>

    <div class="grid-two business-section">
      <section class="panel">
        <h2>Top products</h2>

        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Product</th>
                <th>Quantity</th>
                <th>Revenue</th>
                <th>Profit</th>
              </tr>
            </thead>

            <tbody>
              ${
                report.topProducts.length
                  ? report.topProducts.map((product) => `
                      <tr>
                        <td>
                          <strong>
                            ${escapeHtml(product.productName)}
                          </strong>
                          <br>
                          <small>${escapeHtml(product.sku)}</small>
                        </td>

                        <td>${product.quantitySold}</td>
                        <td>${money(product.revenueMinor)}</td>
                        <td>${money(product.grossProfitMinor)}</td>
                      </tr>
                    `).join("")
                  : `
                    <tr>
                      <td colspan="4">
                        No sales in this period.
                      </td>
                    </tr>
                  `
              }
            </tbody>
          </table>
        </div>
      </section>

      <section class="panel">
        <h2>Teller performance</h2>

        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Teller</th>
                <th>Sales</th>
                <th>Revenue</th>
              </tr>
            </thead>

            <tbody>
              ${
                report.tellerPerformance.length
                  ? report.tellerPerformance.map((teller) => `
                      <tr>
                        <td>
                          <strong>
                            ${escapeHtml(teller.tellerName)}
                          </strong>
                        </td>
                        <td>${teller.salesCount}</td>
                        <td>${money(teller.revenueMinor)}</td>
                      </tr>
                    `).join("")
                  : `
                    <tr>
                      <td colspan="3">
                        No teller activity in this period.
                      </td>
                    </tr>
                  `
              }
            </tbody>
          </table>
        </div>
      </section>
    </div>
  `;

  $("#reportForm").addEventListener(
    "submit",
    async (event) => {
      event.preventDefault();

      try {
        await renderReports(
          $("#reportFrom").value,
          $("#reportTo").value
        );
      } catch (error) {
        handleError(error);
      }
    }
  );
}

document.addEventListener(
  "click",
  (event) => {
    const supplier = event.target.closest(
      "[data-edit-supplier]"
    );

    const removePurchaseLine = event.target.closest(
      "[data-remove-purchase-line]"
    );

    const expense = event.target.closest(
      "[data-void-expense]"
    );

    if (supplier) {
      editSupplier(supplier.dataset.editSupplier);
    }

    if (removePurchaseLine) {
      businessUi.purchaseLines =
        businessUi.purchaseLines.filter(
          (line) =>
            line.id !==
            removePurchaseLine.dataset.removePurchaseLine
        );

      drawPurchaseLines();
    }

    if (expense) {
      voidExpense(expense.dataset.voidExpense);
    }
  }
);

document.addEventListener(
  "change",
  (event) => {
    const productId =
      event.target.dataset.purchaseProduct;

    if (productId) {
      const product = state.inventory.find(
        (item) => item.id === event.target.value
      );

      updatePurchaseLine(
        productId,
        "productId",
        event.target.value
      );

      if (product) {
        updatePurchaseLine(
          productId,
          "unitCostMinor",
          product.costPriceMinor
        );

        drawPurchaseLines();
      }
    }
  }
);

document.addEventListener(
  "input",
  (event) => {
    const quantityId =
      event.target.dataset.purchaseQuantity;

    const costId =
      event.target.dataset.purchaseCost;

    const batchId =
      event.target.dataset.purchaseBatch;

    const expiryId =
      event.target.dataset.purchaseExpiry;

    if (quantityId) {
      updatePurchaseLine(
        quantityId,
        "quantityBaseUnits",
        Math.max(1, Number(event.target.value))
      );
    }

    if (costId) {
      updatePurchaseLine(
        costId,
        "unitCostMinor",
        Math.max(0, Number(event.target.value))
      );
    }

    if (batchId) {
      updatePurchaseLine(
        batchId,
        "batchNumber",
        event.target.value
      );
    }

    if (expiryId) {
      updatePurchaseLine(
        expiryId,
        "expiryDate",
        event.target.value
      );
    }
  }
);
