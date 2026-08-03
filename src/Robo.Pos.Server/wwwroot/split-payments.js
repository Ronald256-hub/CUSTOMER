"use strict";

(function installSplitPaymentCheckout() {
  const originalRenderSales = window.renderSales;
  if (typeof originalRenderSales !== "function") return;

  const methods = [
    ["cash", "Cash"],
    ["mobile_money", "Mobile money"],
    ["card", "Card"],
    ["bank", "Bank"]
  ];
  let observer = null;

  const amount = (value) => Math.max(0, Math.round(Number(value || 0)));
  const format = (value) => `${amount(value).toLocaleString("en-UG")} UGX`;
  const methodOptions = (selected) => methods.map(([value, label]) =>
    `<option value="${value}" ${value === selected ? "selected" : ""}>${label}</option>`
  ).join("");

  function paymentRows() {
    return [...document.querySelectorAll("[data-split-payment-row]")];
  }

  function currentTotal() {
    return amount(calculateCartTotal());
  }

  function readPayments() {
    return paymentRows().map((row) => ({
      paymentMethod: row.querySelector("[data-payment-method]").value,
      amountMinor: amount(row.querySelector("[data-payment-amount]").value),
      reference: row.querySelector("[data-payment-reference]").value.trim()
    }));
  }

  function updateSummary() {
    const total = currentTotal();
    const payments = readPayments();
    const tendered = payments.reduce((sum, payment) => sum + payment.amountMinor, 0);
    const remaining = Math.max(0, total - tendered);
    const change = Math.max(0, tendered - total);
    const remainingHost = document.querySelector("#splitRemaining");
    const changeHost = document.querySelector("#splitChange");
    const tenderedHost = document.querySelector("#splitTendered");
    const summaryHost = document.querySelector("#workspacePaymentSummary");
    if (remainingHost) remainingHost.textContent = format(remaining);
    if (changeHost) changeHost.textContent = format(change);
    if (tenderedHost) tenderedHost.textContent = format(tendered);
    if (summaryHost) {
      summaryHost.textContent = payments.length > 1
        ? `${payments.length} payment methods`
        : methods.find(([value]) => value === payments[0]?.paymentMethod)?.[1] || "Cash";
    }
    document.querySelector("#addSplitPayment")?.toggleAttribute("disabled", payments.length >= methods.length);
    paymentRows().forEach((row) => row.querySelector("[data-remove-payment]")
      ?.toggleAttribute("disabled", payments.length === 1));
  }

  function addPaymentRow(method = "cash", paymentAmount = 0) {
    const host = document.querySelector("#splitPaymentRows");
    if (!host) return;
    const row = document.createElement("div");
    row.className = "split-payment-row";
    row.dataset.splitPaymentRow = "true";
    row.innerHTML = `
      <label>Method<select data-payment-method>${methodOptions(method)}</select></label>
      <label>Amount (UGX)<input data-payment-amount type="number" min="1" step="1" value="${amount(paymentAmount)}" required></label>
      <label>Reference<input data-payment-reference maxlength="120" placeholder="Transaction or bank reference"></label>
      <button data-remove-payment type="button" aria-label="Remove payment method">Remove</button>
    `;
    host.appendChild(row);
    updateSummary();
  }

  function unusedMethod() {
    const used = new Set(readPayments().map((payment) => payment.paymentMethod));
    return methods.find(([value]) => !used.has(value))?.[0] || null;
  }

  async function submitSplitSale(event) {
    event.preventDefault();
    if (!state.cart.size) {
      showMessage("Add at least one product.", true);
      return;
    }

    const payments = readPayments();
    const methodSet = new Set(payments.map((payment) => payment.paymentMethod));
    if (methodSet.size !== payments.length) {
      showMessage("Use each payment method only once.", true);
      return;
    }
    if (payments.some((payment) => payment.amountMinor <= 0)) {
      showMessage("Every payment amount must be greater than zero.", true);
      return;
    }

    const total = currentTotal();
    const tendered = payments.reduce((sum, payment) => sum + payment.amountMinor, 0);
    if (tendered < total) {
      showMessage(`Payment is short by ${format(total - tendered)}.`, true);
      return;
    }
    if (tendered > total && !payments.some((payment) => payment.paymentMethod === "cash")) {
      showMessage("Only cash can exceed the balance because change must be returned.", true);
      return;
    }

    const submit = event.currentTarget.querySelector('button[type="submit"]');
    submit.disabled = true;
    try {
      const result = await api("/api/v3/sales", {
        method: "POST",
        body: JSON.stringify({
          items: [...state.cart.values()].map(({ product, quantity }) => ({
            productId: product.id,
            quantity
          })),
          paymentMethod: payments[0].paymentMethod,
          amountReceivedMinor: tendered,
          payments,
          issueInvoice: document.querySelector("#issueInvoice").checked,
          customerName: document.querySelector("#customerName")?.value.trim() || "",
          customerPhone: document.querySelector("#customerPhone")?.value.trim() || "",
          customerAddress: document.querySelector("#customerAddress")?.value.trim() || "",
          customerTaxNumber: "",
          notes: document.querySelector("#saleNotes")?.value.trim() || ""
        })
      });

      state.cart.clear();
      const tenderLabel = result.payments?.length > 1 ? `${result.payments.length} tenders` : "payment";
      showMessage(`Sale completed with ${tenderLabel}. Receipt ${result.receiptNumber}. Change: ${format(result.changeMinor)}.`);
      await window.renderSales();
    } catch (error) {
      handleError(error);
      submit.disabled = false;
    }
  }

  function installForm() {
    const existing = document.querySelector("#checkoutForm");
    if (!existing) return;

    const form = existing.cloneNode(false);
    form.id = "checkoutForm";
    form.className = "split-checkout-form";
    form.innerHTML = `
      <div class="workspace-payment-banner">
        <span>Multi-tender checkout</span>
        <strong>Split payment enabled</strong>
      </div>
      <div id="splitPaymentRows" class="split-payment-rows"></div>
      <button id="addSplitPayment" type="button">+ Add another payment method</button>
      <section class="split-payment-summary" aria-label="Payment reconciliation">
        <div><span>Sale total</span><strong>${format(currentTotal())}</strong></div>
        <div><span>Tendered</span><strong id="splitTendered">0 UGX</strong></div>
        <div><span>Remaining</span><strong id="splitRemaining">0 UGX</strong></div>
        <div><span>Change</span><strong id="splitChange">0 UGX</strong></div>
      </section>
      <label class="split-invoice-toggle"><span><input id="issueInvoice" type="checkbox"> Create customer invoice</span></label>
      <div id="customerFields" class="hidden split-customer-fields">
        <label>Customer name<input id="customerName" maxlength="150"></label>
        <label>Customer phone<input id="customerPhone" maxlength="50"></label>
        <label>Customer address<input id="customerAddress" maxlength="250"></label>
      </div>
      <label>Sale notes<textarea id="saleNotes" rows="2" maxlength="500"></textarea></label>
      <button class="primary full" type="submit" ${state.shift ? "" : "disabled"}>Complete reconciled sale and issue receipt</button>
    `;
    existing.replaceWith(form);

    addPaymentRow("cash", currentTotal());
    form.addEventListener("submit", submitSplitSale);
    form.addEventListener("input", updateSummary);
    form.addEventListener("change", (event) => {
      if (event.target.id === "issueInvoice") {
        form.querySelector("#customerFields").classList.toggle("hidden", !event.target.checked);
      }
      updateSummary();
    });
    form.addEventListener("click", (event) => {
      const remove = event.target.closest("[data-remove-payment]");
      if (remove) {
        remove.closest("[data-split-payment-row]")?.remove();
        updateSummary();
      }
    });
    form.querySelector("#addSplitPayment").addEventListener("click", () => {
      const next = unusedMethod();
      if (next) addPaymentRow(next, 0);
    });

    observer?.disconnect();
    const totalHost = document.querySelector("#cartTotal");
    if (totalHost) {
      observer = new MutationObserver(() => {
        const rows = paymentRows();
        const firstAmount = rows[0]?.querySelector("[data-payment-amount]");
        if (rows.length === 1 && firstAmount && firstAmount.dataset.manuallyEdited !== "true") {
          firstAmount.value = currentTotal();
        }
        updateSummary();
      });
      observer.observe(totalHost, { childList: true, characterData: true, subtree: true });
    }
    form.addEventListener("input", (event) => {
      if (event.target.matches("[data-payment-amount]")) event.target.dataset.manuallyEdited = "true";
    });
    updateSummary();
  }

  window.renderSales = async function renderSplitPaymentSales() {
    await originalRenderSales();
    installForm();
  };
})();
