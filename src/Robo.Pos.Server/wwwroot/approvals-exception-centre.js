"use strict";

(function installApprovalsExceptionCentre() {
  const workspace = {
    filter: "all",
    search: "",
    severity: "all",
    items: [],
    visibleItems: [],
    rendering: false,
    lastRefresh: null
  };

  const severityRank = {
    critical: 4,
    high: 3,
    medium: 2,
    low: 1,
    information: 0
  };

  const categoryLabels = {
    approval: "Approvals",
    stock: "Stock",
    finance: "Finance",
    customer: "Customer",
    workforce: "Workforce",
    operations: "Operations",
    platform: "Platform"
  };

  const esc = (value) => typeof window.escapeHtml === "function"
    ? window.escapeHtml(String(value ?? ""))
    : String(value ?? "")
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;")
      .replaceAll("'", "&#039;");

  const num = (value, digits = 0) =>
    Number(value || 0).toLocaleString("en-UG", { maximumFractionDigits: digits });

  const money = (value) => `${num(value)} UGX`;

  const localDate = (date = new Date()) => {
    const adjusted = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
    return adjusted.toISOString().slice(0, 10);
  };

  const dateTime = (value) => {
    if (!value) return "Not recorded";
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime())
      ? String(value)
      : parsed.toLocaleString("en-UG", { dateStyle: "medium", timeStyle: "short" });
  };

  const firstNumber = (source, keys, fallback = 0) => {
    for (const key of keys) {
      const value = source?.[key];
      if (value !== undefined && value !== null && Number.isFinite(Number(value))) {
        return Number(value);
      }
    }
    return fallback;
  };

  const rows = (source, keys) => {
    for (const key of keys) {
      if (Array.isArray(source?.[key])) return source[key];
    }
    return Array.isArray(source) ? source : [];
  };

  async function safeApi(path) {
    try {
      return { ok: true, data: await api(path) };
    } catch (error) {
      if (error?.status === 401) throw error;
      return { ok: false, data: {}, error };
    }
  }

  function makeItem({
    id,
    category,
    severity,
    title,
    detail,
    value = "",
    status = "",
    dueAt = null,
    module,
    actionLabel = "Open workflow",
    approval = false,
    source = ""
  }) {
    return {
      id: String(id || `${category}-${title}`),
      category,
      severity,
      title,
      detail,
      value,
      status,
      dueAt,
      module,
      actionLabel,
      approval,
      source,
      rank: severityRank[severity] ?? 0
    };
  }

  function parseDate(value) {
    if (!value) return null;
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? null : parsed;
  }

  function daysUntil(value) {
    const due = parseDate(value);
    if (!due) return null;
    const now = new Date();
    const midnight = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    const dueMidnight = new Date(due.getFullYear(), due.getMonth(), due.getDate());
    return Math.floor((dueMidnight - midnight) / 86400000);
  }

  function normaliseStatus(value) {
    return String(value || "").trim().toLowerCase().replaceAll(" ", "_");
  }

  function buildStockItems(inventoryData, reorderData) {
    const products = rows(inventoryData, ["products"]);
    const recommendations = rows(reorderData, ["recommendations", "items", "products"]);
    const output = [];

    products.forEach((product) => {
      const available = firstNumber(product, [
        "availableBaseUnits",
        "quantityBaseUnits",
        "stockQuantity",
        "currentQuantityBaseUnits"
      ]);
      const threshold = firstNumber(product, [
        "reorderLevelBaseUnits",
        "lowStockThreshold",
        "lowStockThresholdBaseUnits"
      ]);
      const low = Boolean(product.isLowStock) || available <= threshold;
      if (!low) return;

      const shortGlass = product.productType === "short_glass" && Number(product.glassSizeMl) > 0;
      const visibleQuantity = shortGlass
        ? `${num(Math.floor(available / Number(product.glassSizeMl)))} glasses`
        : `${num(available)} ${product.stockUnit || "units"}`;

      output.push(makeItem({
        id: `stock-${product.id}`,
        category: "stock",
        severity: available <= 0 ? "critical" : "high",
        title: `${product.name || product.sku} is ${available <= 0 ? "out of stock" : "below threshold"}`,
        detail: `${product.sku || "No SKU"} · reorder level ${num(threshold)} base units`,
        value: visibleQuantity,
        status: available <= 0 ? "Out of stock" : "Low stock",
        module: "inventory",
        actionLabel: "Review stock",
        source: "Inventory"
      }));
    });

    recommendations.forEach((item) => {
      const suggested = firstNumber(item, [
        "suggestedQuantityBaseUnits",
        "recommendedQuantityBaseUnits",
        "quantityToOrderBaseUnits"
      ]);
      if (suggested <= 0) return;

      output.push(makeItem({
        id: `reorder-${item.productId || item.id || item.sku}`,
        category: "stock",
        severity: normaliseStatus(item.urgency) === "urgent" || item.isBelowReorderLevel
          ? "high"
          : "medium",
        title: `Replenish ${item.productName || item.sku || "product"}`,
        detail: `${num(firstNumber(item, ["availableBaseUnits", "currentQuantityBaseUnits"]))} available · ${num(suggested)} suggested`,
        value: `${num(suggested)} units`,
        status: item.urgency || "Recommended",
        module: "procurement",
        actionLabel: "Open procurement",
        source: "Reorder recommendation"
      }));
    });

    return output;
  }

  function buildProcurementItems(orderData) {
    return rows(orderData, ["purchaseOrders", "orders"]).flatMap((order) => {
      const status = normaliseStatus(order.status);
      const approval = ["submitted", "pending_approval", "awaiting_approval"].includes(status);
      const execution = ["approved", "partially_received"].includes(status);
      if (!approval && !execution) return [];

      return [makeItem({
        id: `purchase-order-${order.id}`,
        category: approval ? "approval" : "stock",
        severity: approval ? "high" : "medium",
        title: approval
          ? `Purchase order ${order.purchaseOrderNumber || order.id} awaits approval`
          : `Purchase order ${order.purchaseOrderNumber || order.id} needs receiving`,
        detail: `${order.supplierName || "Supplier"} · ${order.orderDate || "date not recorded"}`,
        value: money(order.totalMinor),
        status: order.status || "Submitted",
        dueAt: order.expectedDeliveryDate || null,
        module: "procurement",
        actionLabel: approval ? "Review approval" : "Review receiving",
        approval,
        source: "Purchase order"
      })];
    });
  }

  function buildFinanceItems(receivablesData, payablesData) {
    const output = [];
    const collect = (source, type) => {
      rows(source, [type === "receivable" ? "receivables" : "payables"]).forEach((item) => {
        const outstanding = firstNumber(item, [
          "outstandingAmountMinor",
          "remainingAmountMinor",
          "outstandingMinor"
        ]);
        if (outstanding <= 0) return;

        const days = daysUntil(item.dueDate);
        if (days !== null && days > 7) return;

        const overdue = days !== null && days < 0;
        const dueSoon = days !== null && days <= 7;
        output.push(makeItem({
          id: `${type}-${item.id}`,
          category: "finance",
          severity: overdue ? "critical" : dueSoon ? "high" : "medium",
          title: overdue
            ? `${type === "receivable" ? "Customer balance" : "Supplier balance"} is overdue`
            : `${type === "receivable" ? "Customer receipt" : "Supplier payment"} is due soon`,
          detail: `${type === "receivable" ? item.customerName : item.supplierName} · ${item.documentNumber || item.supplierInvoiceNumber || "open item"}`,
          value: money(outstanding),
          status: overdue
            ? `${Math.abs(days)} day${Math.abs(days) === 1 ? "" : "s"} overdue`
            : days === 0 ? "Due today" : days === null ? "Open" : `Due in ${days} days`,
          dueAt: item.dueDate || null,
          module: "finance",
          actionLabel: type === "receivable" ? "Collect payment" : "Prepare payment",
          source: type === "receivable" ? "Receivable" : "Payable"
        }));
      });
    };
    collect(receivablesData, "receivable");
    collect(payablesData, "payable");
    return output;
  }

  function buildCustomerItems(taskData, quotationData) {
    const output = [];
    rows(taskData, ["tasks"]).forEach((task) => {
      const status = normaliseStatus(task.status);
      if (!["open", "in_progress", "pending"].includes(status)) return;
      const days = daysUntil(task.dueAtUtc || task.dueDate);
      if (days !== null && days > 3) return;
      const overdue = days !== null && days < 0;
      output.push(makeItem({
        id: `crm-task-${task.id}`,
        category: "customer",
        severity: overdue || normaliseStatus(task.priority) === "urgent"
          ? "critical"
          : normaliseStatus(task.priority) === "high" || days === 0
            ? "high"
            : "medium",
        title: task.title || "Customer follow-up",
        detail: `${task.customerName || "General business task"} · assigned to ${task.assignedToName || "current user"}`,
        value: "",
        status: overdue
          ? `${Math.abs(days)} day${Math.abs(days) === 1 ? "" : "s"} overdue`
          : days === 0 ? "Due today" : days === null ? task.status || "Open" : `Due in ${days} days`,
        dueAt: task.dueAtUtc || task.dueDate || null,
        module: "crm",
        actionLabel: "Open follow-up",
        source: "CRM task"
      }));
    });

    rows(quotationData, ["quotations"]).forEach((quote) => {
      const status = normaliseStatus(quote.status);
      if (!["draft", "sent", "accepted"].includes(status)) return;
      const expired = Boolean(quote.isPastValidity) || (daysUntil(quote.validUntil) ?? 1) < 0;
      if (!expired) return;
      output.push(makeItem({
        id: `quotation-${quote.id}`,
        category: "customer",
        severity: status === "accepted" ? "high" : "medium",
        title: `Quotation ${quote.quotationNumber || quote.id} needs attention`,
        detail: `${quote.customerName || "Customer"} · validity ended ${quote.validUntil || "without a date"}`,
        value: money(quote.totalMinor),
        status: status === "accepted" ? "Accepted but not converted" : "Past validity",
        dueAt: quote.validUntil || null,
        module: "crm",
        actionLabel: "Review quotation",
        source: "Quotation"
      }));
    });

    return output;
  }

  function buildWorkforceItems(attendanceData, leaveData, payrollData) {
    const output = [];

    rows(attendanceData, ["attendance", "records"]).forEach((record) => {
      const status = normaliseStatus(record.status);
      const requiresApproval =
        ["submitted", "pending", "pending_approval", "clocked_out"].includes(status) ||
        (record.clockOutAtUtc && !record.approvedAtUtc && status !== "approved");
      if (!requiresApproval) return;

      output.push(makeItem({
        id: `attendance-${record.id}`,
        category: "approval",
        severity: "high",
        title: `Attendance approval for ${record.employeeName || "employee"}`,
        detail: `${dateTime(record.clockInAtUtc)} to ${dateTime(record.clockOutAtUtc)}`,
        value: record.workedMinutes ? `${num(Number(record.workedMinutes) / 60, 1)} hours` : "",
        status: record.status || "Pending approval",
        dueAt: record.attendanceDate || record.clockOutAtUtc || null,
        module: "hrm",
        actionLabel: "Review attendance",
        approval: true,
        source: "Attendance"
      }));
    });

    rows(leaveData, ["leaveRequests"]).forEach((request) => {
      if (!["submitted", "pending_approval"].includes(normaliseStatus(request.status))) return;
      output.push(makeItem({
        id: `leave-${request.id}`,
        category: "approval",
        severity: daysUntil(request.startDate) !== null && daysUntil(request.startDate) <= 2
          ? "critical"
          : "high",
        title: `Leave request from ${request.employeeName || "employee"}`,
        detail: `${request.leaveTypeName || "Leave"} · ${request.startDate || ""} to ${request.endDate || ""}`,
        value: `${num(request.requestedDays, 1)} days`,
        status: request.status || "Submitted",
        dueAt: request.startDate || null,
        module: "hrm",
        actionLabel: "Review leave",
        approval: true,
        source: "Leave request"
      }));
    });

    rows(payrollData, ["payrollPeriods"]).forEach((period) => {
      const status = normaliseStatus(period.status);
      if (!["calculated", "submitted", "pending_approval"].includes(status)) return;
      output.push(makeItem({
        id: `payroll-${period.id}`,
        category: "approval",
        severity: "high",
        title: `Payroll period ${period.name || period.periodName || period.id} awaits approval`,
        detail: `${period.startDate || ""} to ${period.endDate || ""}`,
        value: money(firstNumber(period, ["netPayMinor", "totalNetPayMinor", "grossPayMinor"])),
        status: period.status || "Calculated",
        dueAt: period.endDate || null,
        module: "hrm",
        actionLabel: "Review payroll",
        approval: true,
        source: "Payroll"
      }));
    });

    return output;
  }

  function buildOperationalItems(summaryData, subscriptionData) {
    const output = [];
    if (Number(summaryData?.openShifts || 0) === 0) {
      output.push(makeItem({
        id: "no-open-shift",
        category: "operations",
        severity: "medium",
        title: "No teller shift is currently open",
        detail: "Sales cannot be completed until an authorised teller opens a branch shift.",
        value: "",
        status: "Action required",
        module: "sales",
        actionLabel: "Open point of sale",
        source: "Shift control"
      }));
    }

    const subscription = subscriptionData || {};
    const status = normaliseStatus(subscription.status);
    if (status && !["active", "trial"].includes(status)) {
      output.push(makeItem({
        id: "subscription-status",
        category: "platform",
        severity: ["suspended", "expired", "cancelled"].includes(status) ? "critical" : "high",
        title: `Tenant subscription status is ${subscription.status}`,
        detail: `${subscription.planName || subscription.planCode || "Current plan"} requires platform review.`,
        value: "",
        status: subscription.status,
        module: "saas",
        actionLabel: "Review tenant",
        source: "SaaS"
      }));
    }
    return output;
  }

  async function loadItems() {
    const today = localDate();
    const [
      summary,
      inventory,
      reorder,
      purchaseOrders,
      receivables,
      payables,
      tasks,
      quotations,
      attendance,
      leaveRequests,
      payroll,
      subscription
    ] = await Promise.all([
      safeApi("/api/v3/admin/summary"),
      safeApi("/api/v3/admin/inventory/products"),
      safeApi("/api/v3/procurement/reorder-recommendations"),
      safeApi("/api/v3/procurement/purchase-orders?limit=100"),
      safeApi("/api/v3/finance/receivables?status=open&limit=100"),
      safeApi("/api/v3/finance/payables?status=open&limit=100"),
      safeApi("/api/v3/crm/tasks?limit=100"),
      safeApi("/api/v3/crm/quotations?limit=100"),
      safeApi(`/api/v3/hrm/attendance?fromDate=${today}&toDate=${today}`),
      safeApi("/api/v3/hrm/leave-requests"),
      safeApi("/api/v3/hrm/payroll-periods"),
      safeApi("/api/v3/saas/tenant/subscription")
    ]);

    const items = [
      ...buildStockItems(inventory.data, reorder.data),
      ...buildProcurementItems(purchaseOrders.data),
      ...buildFinanceItems(receivables.data, payables.data),
      ...buildCustomerItems(tasks.data, quotations.data),
      ...buildWorkforceItems(attendance.data, leaveRequests.data, payroll.data),
      ...buildOperationalItems(summary.data || {}, subscription.data || {})
    ];

    const unique = new Map();
    items.forEach((item) => unique.set(item.id, item));

    workspace.items = [...unique.values()].sort((left, right) => {
      if (right.rank !== left.rank) return right.rank - left.rank;
      const leftDue = parseDate(left.dueAt)?.getTime() ?? Number.MAX_SAFE_INTEGER;
      const rightDue = parseDate(right.dueAt)?.getTime() ?? Number.MAX_SAFE_INTEGER;
      return leftDue - rightDue || left.title.localeCompare(right.title);
    });
    workspace.lastRefresh = new Date();
  }

  function filteredItems() {
    const query = workspace.search.trim().toLowerCase();
    return workspace.items.filter((item) => {
      const filterMatch =
        workspace.filter === "all" ||
        (workspace.filter === "approval" && item.approval) ||
        item.category === workspace.filter;
      const severityMatch =
        workspace.severity === "all" ||
        item.severity === workspace.severity ||
        (workspace.severity === "urgent" && item.rank >= severityRank.high);
      const searchMatch =
        !query ||
        `${item.title} ${item.detail} ${item.value} ${item.status} ${item.source}`
          .toLowerCase()
          .includes(query);
      return filterMatch && severityMatch && searchMatch;
    });
  }

  function statusBadge(item) {
    return `<span class="exception-severity ${esc(item.severity)}">${esc(item.severity)}</span>`;
  }

  function metric(label, value, note, tone = "") {
    return `<article class="exception-metric ${esc(tone)}"><span>${esc(label)}</span><strong>${esc(value)}</strong><small>${esc(note)}</small></article>`;
  }

  function updateQueue() {
    const host = document.querySelector("#exceptionQueue");
    if (!host) return;

    workspace.visibleItems = filteredItems();
    const items = workspace.visibleItems;

    host.innerHTML = items.length
      ? items.map((item) => `
        <article class="exception-card severity-${esc(item.severity)}" data-exception-category="${esc(item.category)}">
          <div class="exception-card-marker" aria-hidden="true">${esc((categoryLabels[item.category] || item.category).slice(0, 2).toUpperCase())}</div>
          <div class="exception-card-main">
            <div class="exception-card-heading">
              <div>
                <span class="exception-source">${esc(item.source || categoryLabels[item.category] || item.category)}</span>
                <h3>${esc(item.title)}</h3>
              </div>
              ${statusBadge(item)}
            </div>
            <p>${esc(item.detail)}</p>
            <div class="exception-card-meta">
              ${item.status ? `<span><b>Status</b>${esc(item.status)}</span>` : ""}
              ${item.value ? `<span><b>Exposure</b>${esc(item.value)}</span>` : ""}
              ${item.dueAt ? `<span><b>Due</b>${esc(dateTime(item.dueAt))}</span>` : ""}
              <span><b>Control</b>${item.approval ? "Approval required" : "Operational review"}</span>
            </div>
          </div>
          <div class="exception-card-action">
            <button type="button" data-page="${esc(item.module)}">${esc(item.actionLabel)}</button>
          </div>
        </article>
      `).join("")
      : `<div class="exception-empty"><span aria-hidden="true">✓</span><strong>No matching exception</strong><p>Change the filters or refresh the centre. A clear queue means there is no detected item in the selected view.</p></div>`;

    const count = document.querySelector("#exceptionVisibleCount");
    if (count) count.textContent = `${num(items.length)} visible item${items.length === 1 ? "" : "s"}`;

    document.querySelectorAll("[data-exception-filter]").forEach((button) => {
      button.setAttribute("aria-selected", String(button.dataset.exceptionFilter === workspace.filter));
    });
  }

  function exportCsv() {
    const items = workspace.visibleItems.length ? workspace.visibleItems : filteredItems();
    const quote = (value) => `"${String(value ?? "").replaceAll('"', '""')}"`;
    const lines = [
      ["Severity", "Category", "Source", "Title", "Detail", "Status", "Exposure", "Due", "Workflow"].map(quote).join(","),
      ...items.map((item) => [
        item.severity,
        categoryLabels[item.category] || item.category,
        item.source,
        item.title,
        item.detail,
        item.status,
        item.value,
        item.dueAt || "",
        item.module
      ].map(quote).join(","))
    ];

    const blob = new Blob([lines.join("\r\n")], { type: "text/csv;charset=utf-8" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = `nexus-approvals-exceptions-${localDate()}.csv`;
    document.body.appendChild(link);
    link.click();
    URL.revokeObjectURL(link.href);
    link.remove();
  }

  async function render() {
    if (workspace.rendering) return;
    workspace.rendering = true;
    const page = document.querySelector("#page");
    if (!page) {
      workspace.rendering = false;
      return;
    }

    page.innerHTML = `<div class="page-loading" aria-live="polite" aria-busy="true"><div class="skeleton"></div><div class="skeleton" style="min-height:420px"></div></div>`;

    try {
      await loadItems();
      const urgent = workspace.items.filter((item) => item.rank >= severityRank.high).length;
      const critical = workspace.items.filter((item) => item.severity === "critical").length;
      const approvals = workspace.items.filter((item) => item.approval).length;
      const finance = workspace.items.filter((item) => item.category === "finance").length;
      const stock = workspace.items.filter((item) => item.category === "stock").length;
      const workforce = workspace.items.filter((item) =>
        item.category === "workforce" || (item.category === "approval" && ["Attendance", "Leave request", "Payroll"].includes(item.source))
      ).length;

      page.innerHTML = `
        <div class="approvals-exception-workspace">
          <section class="exception-hero">
            <div>
              <span class="workspace-eyebrow">NEXUS POS 6.6 · CONTROLLED ACTION MANAGEMENT</span>
              <h2>Approvals and exception centre</h2>
              <p>See the most important approvals, overdue balances, stock risks, customer commitments and workforce actions before they become losses or delays.</p>
            </div>
            <div class="exception-hero-actions">
              <button id="exceptionRefresh" class="primary" type="button">Refresh centre</button>
              <button id="exceptionExport" type="button">Export CSV</button>
              <button id="exceptionPrint" type="button">Print</button>
            </div>
          </section>

          <section class="exception-metrics" aria-label="Approvals and exception metrics">
            ${metric("Detected items", num(workspace.items.length), "Across all monitored workflows")}
            ${metric("Critical", num(critical), "Immediate intervention", critical ? "danger" : "success")}
            ${metric("Urgent or high", num(urgent), "Prioritised review", urgent ? "warning" : "success")}
            ${metric("Approvals", num(approvals), "Maker-checker decisions", approvals ? "blue" : "success")}
            ${metric("Finance actions", num(finance), "Debt and payment exposure", finance ? "warning" : "")}
            ${metric("Stock actions", num(stock), "Low stock and replenishment", stock ? "warning" : "")}
            ${metric("Workforce actions", num(workforce), "Attendance, leave and payroll", workforce ? "blue" : "")}
          </section>

          <section class="panel exception-control-panel">
            <div class="exception-section-heading">
              <div>
                <h2>Priority queue</h2>
                <p>Sorted by severity, due date and workflow. Decisions remain inside the authoritative operational module.</p>
              </div>
              <div>
                <span id="exceptionVisibleCount">0 visible items</span>
                <small>Last refreshed ${esc(dateTime(workspace.lastRefresh))}</small>
              </div>
            </div>

            <div class="exception-controls">
              <label>
                <span>Search queue</span>
                <input id="exceptionSearch" type="search" placeholder="Customer, supplier, product, employee or document" autocomplete="off">
              </label>
              <label>
                <span>Severity</span>
                <select id="exceptionSeverity">
                  <option value="all">All severities</option>
                  <option value="urgent">Urgent and high</option>
                  <option value="critical">Critical only</option>
                  <option value="high">High only</option>
                  <option value="medium">Medium only</option>
                </select>
              </label>
            </div>

            <div class="exception-tabs" role="tablist" aria-label="Exception categories">
              ${[
                ["all", "All"],
                ["approval", "Approvals"],
                ["stock", "Stock"],
                ["finance", "Finance"],
                ["customer", "Customer"],
                ["workforce", "Workforce"],
                ["operations", "Operations"],
                ["platform", "Platform"]
              ].map(([id, label]) =>
                `<button type="button" role="tab" data-exception-filter="${id}" aria-selected="${workspace.filter === id}">${label}</button>`
              ).join("")}
            </div>

            <div id="exceptionQueue" class="exception-queue" aria-live="polite"></div>
          </section>

          <section class="exception-governance">
            <article><span>1</span><div><strong>Detect</strong><small>Read-only signals from existing operational APIs.</small></div></article>
            <article><span>2</span><div><strong>Prioritise</strong><small>Critical, high and medium actions sorted automatically.</small></div></article>
            <article><span>3</span><div><strong>Review</strong><small>Open the authoritative module with its permissions and versions.</small></div></article>
            <article><span>4</span><div><strong>Audit</strong><small>Existing maker-checker and immutable history remain enforced.</small></div></article>
          </section>
        </div>`;

      updateQueue();
    } catch (error) {
      if (typeof window.handleError === "function") window.handleError(error);
      page.innerHTML = `<section class="panel"><h2>Approvals and exception centre could not load</h2><p>${esc(error.message || "The monitored workflows are currently unavailable.")}</p><button class="primary" type="button" data-page="dashboard">Return to command centre</button></section>`;
    } finally {
      workspace.rendering = false;
    }
  }

  document.addEventListener("click", (event) => {
    const filter = event.target.closest("[data-exception-filter]");
    if (filter) {
      workspace.filter = filter.dataset.exceptionFilter;
      updateQueue();
      return;
    }

    if (event.target.closest("#exceptionRefresh")) {
      render();
      return;
    }

    if (event.target.closest("#exceptionExport")) {
      exportCsv();
      return;
    }

    if (event.target.closest("#exceptionPrint")) {
      window.print();
    }
  });

  document.addEventListener("input", (event) => {
    if (event.target.id === "exceptionSearch") {
      workspace.search = event.target.value;
      updateQueue();
    }
  });

  document.addEventListener("change", (event) => {
    if (event.target.id === "exceptionSeverity") {
      workspace.severity = event.target.value;
      updateQueue();
    }
  });

  window.NexusApprovalsExceptionCentre = {
    render,
    isRendering: () => workspace.rendering
  };
})();
