"use strict";

(function installNexusExperience() {
  const legacy = {
    enterApplication: window.enterApplication,
    showLogin: window.showLogin,
    renderDashboard: window.renderDashboard,
    renderOwnerDashboard: window.renderOwnerDashboard,
    renderInventory: window.renderInventory,
    renderSuppliers: window.renderSuppliers,
    renderPurchases: window.renderPurchases,
    renderExpenses: window.renderExpenses,
    renderReports: window.renderReports,
    renderSystemAdministration: window.renderSystemAdministration,
    renderSales: window.renderSales,
    renderReceipts: window.renderReceipts,
    renderUsers: window.renderUsers
  };

  const ui = {
    currentPage: "dashboard",
    context: null,
    availableShops: [],
    navigationFilter: "",
    moduleData: new Map(),
    commandOpen: false
  };

  const moduleCatalogue = [
    {
      group: "Overview",
      pages: [
        { id: "dashboard", label: "Command centre", icon: "CC", subtitle: "Live business control" },
        { id: "owner", label: "Owner view", icon: "OV", subtitle: "Read-only performance" }
      ]
    },
    {
      group: "Front office",
      pages: [
        { id: "sales", label: "Point of sale", icon: "POS", subtitle: "Sell and receive payment" },
        { id: "receipts", label: "Receipts & invoices", icon: "RC", subtitle: "Audit documents" },
        { id: "crm", label: "Customers & CRM", icon: "CRM", subtitle: "Customers and follow-ups" }
      ]
    },
    {
      group: "Stock & supply",
      pages: [
        { id: "inventory", label: "Inventory", icon: "ST", subtitle: "Products and stock" },
        { id: "procurement", label: "Procurement", icon: "PO", subtitle: "Orders and replenishment" },
        { id: "purchases", label: "Direct purchases", icon: "GR", subtitle: "Receive supplier stock" },
        { id: "suppliers", label: "Suppliers", icon: "SU", subtitle: "Supplier directory" }
      ]
    },
    {
      group: "Finance",
      pages: [
        { id: "accounting", label: "Accounting", icon: "GL", subtitle: "Ledger and trial balance" },
        { id: "finance", label: "Receivables & cash", icon: "FC", subtitle: "Debtors, creditors and cash" },
        { id: "expenses", label: "Expenses", icon: "EX", subtitle: "Business costs" },
        { id: "reports", label: "Reports", icon: "RP", subtitle: "Performance reporting" }
      ]
    },
    {
      group: "Workforce",
      pages: [
        { id: "hrm", label: "People & HRM", icon: "HR", subtitle: "Employees and payroll" },
        { id: "users", label: "User accounts", icon: "UA", subtitle: "Access and recovery" }
      ]
    },
    {
      group: "Platform",
      pages: [
        { id: "saas", label: "Tenant operations", icon: "SA", subtitle: "Subscription and support" },
        { id: "settings", label: "Settings & backup", icon: "SB", subtitle: "Identity and protection" }
      ]
    }
  ];

  const tellerCatalogue = [
    {
      group: "Workspace",
      pages: [
        { id: "sales", label: "Point of sale", icon: "POS", subtitle: "Sell and receive payment" },
        { id: "receipts", label: "My receipts", icon: "RC", subtitle: "Completed transactions" }
      ]
    }
  ];

  const pageMeta = {
    dashboard: ["Command centre", "Live sales, stock, short-glass quantities and operational alerts"],
    owner: ["Owner view", "Read-only business performance and stock availability"],
    sales: ["Point of sale", "Fast product selection, payment and receipt creation"],
    receipts: ["Receipts & invoices", "Saved thermal receipts, invoices and audit documents"],
    inventory: ["Inventory", "Products, price controls and branch stock"],
    procurement: ["Procurement", "Purchase orders, receiving, batches and replenishment"],
    purchases: ["Direct purchases", "Receive supplier stock into the active branch"],
    suppliers: ["Suppliers", "Supplier contacts, status and purchasing relationships"],
    accounting: ["Accounting", "Chart of accounts, journals and balanced reporting"],
    finance: ["Receivables & cash", "Customer debt, supplier obligations and cash movement"],
    expenses: ["Expenses", "Record and review controlled business costs"],
    reports: ["Reports", "Revenue, gross profit and teller performance"],
    crm: ["Customers & CRM", "Customer value, quotations, tasks and loyalty"],
    hrm: ["People & HRM", "Employees, attendance, leave and payroll readiness"],
    users: ["User accounts", "Account access, roles and password recovery"],
    saas: ["Tenant operations", "Subscription, entitlements, usage and support controls"],
    settings: ["Settings & backup", "Business identity and verified database protection"]
  };

  function allPages() {
    return (state.user?.role === "admin" ? moduleCatalogue : tellerCatalogue)
      .flatMap((group) => group.pages);
  }

  function findPage(pageId) {
    return allPages().find((page) => page.id === pageId);
  }

  function localIsoDate(date = new Date()) {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, "0");
    const day = String(date.getDate()).padStart(2, "0");
    return `${year}-${month}-${day}`;
  }

  function formatNumber(value, maximumFractionDigits = 0) {
    return Number(value || 0).toLocaleString("en-UG", {
      maximumFractionDigits
    });
  }

  function formatDateTime(value) {
    if (!value) {
      return "Not recorded";
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return String(value);
    }

    return date.toLocaleString("en-UG", {
      dateStyle: "medium",
      timeStyle: "short"
    });
  }

  function firstNumber(source, keys, fallback = 0) {
    for (const key of keys) {
      const value = source?.[key];
      if (value !== undefined && value !== null && Number.isFinite(Number(value))) {
        return Number(value);
      }
    }
    return fallback;
  }

  function firstText(source, keys, fallback = "Not available") {
    for (const key of keys) {
      const value = source?.[key];
      if (value !== undefined && value !== null && String(value).trim()) {
        return String(value);
      }
    }
    return fallback;
  }

  async function safeApi(path, options) {
    try {
      return { ok: true, data: await api(path, options) };
    } catch (error) {
      if (error.status === 401) {
        throw error;
      }
      return { ok: false, error };
    }
  }

  function metricCard(label, value, note, tone = "") {
    return `
      <article class="command-metric ${escapeHtml(tone)}">
        <span>${escapeHtml(label)}</span>
        <strong>${escapeHtml(value)}</strong>
        <small>${escapeHtml(note || "")}</small>
      </article>
    `;
  }

  function statusChip(status, preferredTone) {
    const normalized = String(status || "unknown").toLowerCase();
    let tone = preferredTone || "neutral";

    if (!preferredTone) {
      if (["active", "open", "healthy", "balanced", "completed", "approved", "enabled"].includes(normalized)) {
        tone = "success";
      } else if (["warning", "trial", "submitted", "pending", "calculated", "partially_received"].includes(normalized)) {
        tone = "warning";
      } else if (["failed", "suspended", "overdue", "voided", "cancelled", "inactive"].includes(normalized)) {
        tone = "danger";
      }
    }

    return `<span class="status-chip ${tone}">${escapeHtml(status || "Unknown")}</span>`;
  }

  function loadingPage() {
    $("#page").innerHTML = `
      <div class="page-loading" aria-live="polite" aria-busy="true">
        <div class="skeleton"></div>
        <div class="command-metrics">
          <div class="skeleton"></div>
          <div class="skeleton"></div>
          <div class="skeleton"></div>
          <div class="skeleton"></div>
        </div>
        <div class="skeleton" style="min-height:280px"></div>
      </div>
    `;
  }

  function renderEnterpriseNavigation() {
    const catalogue = state.user?.role === "admin" ? moduleCatalogue : tellerCatalogue;
    const query = ui.navigationFilter.trim().toLowerCase();

    $("#navigation").innerHTML = catalogue.map((group) => {
      const pages = group.pages.filter((page) => {
        if (!query) {
          return true;
        }
        return `${page.label} ${page.subtitle} ${group.group}`.toLowerCase().includes(query);
      });

      if (!pages.length) {
        return "";
      }

      return `
        <section class="nav-group" aria-label="${escapeHtml(group.group)}">
          <span class="nav-group-title">${escapeHtml(group.group)}</span>
          ${pages.map((page) => `
            <button
              class="nav-button ${page.id === ui.currentPage ? "active" : ""}"
              data-page="${page.id}"
              type="button"
              title="${escapeHtml(page.label)} — ${escapeHtml(page.subtitle)}"
            >
              <span class="nav-icon" aria-hidden="true">${escapeHtml(page.icon)}</span>
              <span class="nav-label">${escapeHtml(page.label)}</span>
            </button>
          `).join("")}
        </section>
      `;
    }).join("") || `<p class="empty" style="padding:12px">No module matches your search.</p>`;
  }

  function updateActiveNavigation() {
    document.querySelectorAll(".nav-button").forEach((button) => {
      button.classList.toggle("active", button.dataset.page === ui.currentPage);
      if (button.dataset.page === ui.currentPage) {
        button.setAttribute("aria-current", "page");
      } else {
        button.removeAttribute("aria-current");
      }
    });
  }

  function setPageHeader(pageId) {
    const [title, subtitle] = pageMeta[pageId] || ["Nexus POS", "Business operations"];
    $("#pageTitle").textContent = title;
    $("#pageSubtitle").textContent = subtitle;
    document.title = `${title} · Nexus POS`;
  }

  async function openEnterprisePage(pageId) {
    const page = findPage(pageId);
    if (!page) {
      pageId = state.user?.role === "admin" ? "dashboard" : "sales";
    }

    ui.currentPage = pageId;
    setPageHeader(pageId);
    updateActiveNavigation();
    closeMobileSidebar();
    loadingPage();

    const newRenderers = {
      dashboard: renderCommandCentre,
      accounting: () => renderModuleHub("accounting"),
      finance: () => renderModuleHub("finance"),
      procurement: () => renderModuleHub("procurement"),
      crm: () => renderModuleHub("crm"),
      hrm: () => renderModuleHub("hrm"),
      saas: () => renderModuleHub("saas")
    };

    const legacyRenderers = {
      owner: legacy.renderOwnerDashboard,
      inventory: legacy.renderInventory,
      suppliers: legacy.renderSuppliers,
      purchases: legacy.renderPurchases,
      expenses: legacy.renderExpenses,
      reports: legacy.renderReports,
      settings: legacy.renderSystemAdministration,
      sales: legacy.renderSales,
      receipts: legacy.renderReceipts,
      users: legacy.renderUsers
    };

    try {
      const renderer = newRenderers[pageId] || legacyRenderers[pageId];
      if (!renderer) {
        throw new Error(`The ${pageId} module is not available.`);
      }
      await renderer();
      history.replaceState(null, "", `#${pageId}`);
    } catch (error) {
      handleError(error);
      $("#page").innerHTML = `
        <section class="panel">
          <h2>Module could not be opened</h2>
          <p>${escapeHtml(error.message || "The requested module is unavailable.")}</p>
          <button class="primary" type="button" data-page="dashboard">Return to command centre</button>
        </section>
      `;
    }
  }

  async function refreshShellContext() {
    const [contextResult, shopsResult] = await Promise.all([
      safeApi("/api/v3/session/shop-context"),
      safeApi("/api/v3/shops")
    ]);

    if (contextResult.ok) {
      ui.context = contextResult.data;
      const contextHost = $("#shopContext");
      if (contextHost) {
        contextHost.innerHTML = `
          <div class="shop-context-copy">
            <small>Active branch</small>
            <strong>${escapeHtml(ui.context.shopName || ui.context.shopCode || "Main branch")}</strong>
          </div>
        `;
        contextHost.title = `${ui.context.organizationName || "Organisation"} · ${ui.context.shopCode || ""}`;
      }
    }

    if (shopsResult.ok) {
      ui.availableShops = shopsResult.data.shops || [];
    }
  }

  function buildAlerts(summary, shortGlassRows, subscription) {
    const alerts = [];
    const lowStock = Number(summary?.lowStockProducts || 0);

    if (lowStock > 0) {
      alerts.push({
        icon: "!",
        title: `${lowStock} low-stock product${lowStock === 1 ? "" : "s"}`,
        note: "Review inventory and replenishment recommendations.",
        page: "inventory"
      });
    }

    const lowShortGlass = shortGlassRows.filter((row) => row.isLowStock || Number(row.remainingGlasses || 0) <= 5);
    if (lowShortGlass.length) {
      alerts.push({
        icon: "SG",
        title: `${lowShortGlass.length} short-glass line${lowShortGlass.length === 1 ? "" : "s"} need attention`,
        note: "Remaining sellable glasses are at or near the warning level.",
        page: "dashboard"
      });
    }

    if (Number(summary?.openShifts || 0) === 0) {
      alerts.push({
        icon: "SH",
        title: "No teller shift is open",
        note: "Open a shift before completing sales.",
        page: "sales"
      });
    }

    if (subscription && !["active", "trial"].includes(String(subscription.status || "").toLowerCase())) {
      alerts.push({
        icon: "SA",
        title: `Subscription status: ${subscription.status || "unknown"}`,
        note: "Review tenant subscription and entitlement controls.",
        page: "saas"
      });
    }

    if (!alerts.length) {
      alerts.push({
        icon: "OK",
        title: "No critical operational alert",
        note: "Sales, branch stock and platform status are within the monitored thresholds.",
        page: "dashboard",
        positive: true
      });
    }

    return alerts;
  }

  async function renderCommandCentre() {
    const today = localIsoDate();
    const query = new URLSearchParams({ from: today, to: today });

    const [
      summaryResult,
      reportResult,
      inventoryResult,
      shortGlassResult,
      crmResult,
      hrmResult,
      subscriptionResult
    ] = await Promise.all([
      safeApi("/api/v3/admin/summary"),
      safeApi(`/api/v3/admin/reports/summary?${query.toString()}`),
      safeApi("/api/v3/admin/inventory/products"),
      safeApi(`/api/v3/reports/short-glass?fromDate=${today}&toDate=${today}`),
      safeApi("/api/v3/crm/dashboard"),
      safeApi("/api/v3/hrm/dashboard"),
      safeApi("/api/v3/saas/tenant/subscription")
    ]);

    const summary = summaryResult.data || {};
    const report = reportResult.data || {};
    const inventory = inventoryResult.data?.products || [];
    const shortGlassRows = shortGlassResult.data?.products || [];
    const crm = crmResult.data || {};
    const hrm = hrmResult.data || {};
    const subscription = subscriptionResult.data || null;
    const alerts = buildAlerts(summary, shortGlassRows, subscription);
    const context = ui.context || {};

    const activeProducts = Number(summary.activeProducts || inventory.filter((item) => item.isActive !== false).length);
    const revenue = firstNumber(report, ["revenueMinor", "grossSalesMinor", "netSalesMinor", "totalSalesMinor"]);
    const grossProfit = firstNumber(report, ["grossProfitMinor", "profitMinor"]);
    const completedSales = firstNumber(report, ["salesCount", "completedSalesCount"], Number(summary.completedSales || 0));
    const customerCount = firstNumber(crm, ["activeCustomerCount", "customerCount", "totalCustomerCount"]);
    const activeEmployees = firstNumber(hrm, ["activeEmployeeCount", "employeeCount"]);

    $("#page").innerHTML = `
      <div class="nexus-command-centre">
        <section class="command-hero">
          <div>
            <span class="command-eyebrow">Nexus POS 6.1 · operational intelligence</span>
            <h2>Everything requiring attention, on one screen.</h2>
            <p>
              Monitor today’s sales, short-glass quantities, stock alerts, customers,
              workforce readiness and tenant health without opening separate reports.
            </p>
          </div>
          <div class="command-hero-meta">
            <div><span>Organisation</span><strong>${escapeHtml(context.organizationName || "Nexus business")}</strong></div>
            <div><span>Active branch</span><strong>${escapeHtml(context.shopName || context.shopCode || "Main branch")}</strong></div>
            <div><span>Last refreshed</span><strong>${escapeHtml(formatDateTime(new Date()))}</strong></div>
          </div>
        </section>

        <section class="command-metrics" aria-label="Business metrics">
          ${metricCard("Today’s revenue", money(revenue), `${formatNumber(completedSales)} completed sales`, "success")}
          ${metricCard("Gross profit", money(grossProfit), "Before operating expenses", "blue")}
          ${metricCard("Low stock", formatNumber(summary.lowStockProducts), `${formatNumber(activeProducts)} active products`, Number(summary.lowStockProducts) ? "warning" : "success")}
          ${metricCard("Open shifts", formatNumber(summary.openShifts), `${formatNumber(summary.activeUsers)} active users`, Number(summary.openShifts) ? "success" : "warning")}
          ${metricCard("Customers", formatNumber(customerCount), "Active CRM customer base", "blue")}
          ${metricCard("Employees", formatNumber(activeEmployees), "Active workforce records", "blue")}
          ${metricCard("Audit documents", formatNumber(summary.savedDocuments), "Receipts, invoices and PDFs", "")}
          ${metricCard("Subscription", firstText(subscription, ["planName", "planCode"], "Compatibility plan"), firstText(subscription, ["status"], "Active"), "success")}
        </section>

        <section class="command-grid">
          <article class="panel">
            <div class="command-section-heading">
              <div>
                <h3>Priority actions</h3>
                <p>Move directly into the workflow that needs attention.</p>
              </div>
            </div>
            <div class="quick-actions">
              ${[
                ["sales", "New sale", "Start or continue point-of-sale work"],
                ["inventory", "Review stock", "Check low stock and short-glass levels"],
                ["procurement", "Replenish", "Review purchase orders and reorder needs"],
                ["crm", "Customer follow-up", "Open tasks, quotations and loyalty"],
                ["finance", "Collect or pay", "Review debtors, creditors and cash"],
                ["hrm", "Workforce", "Attendance, leave and payroll readiness"]
              ].map(([page, title, note]) => `
                <button class="quick-action" type="button" data-page="${page}">
                  <span>${escapeHtml(page)}</span>
                  <strong>${escapeHtml(title)}</strong>
                  <small>${escapeHtml(note)}</small>
                </button>
              `).join("")}
            </div>
          </article>

          <article class="panel">
            <div class="command-section-heading">
              <div>
                <h3>Operational alerts</h3>
                <p>Current conditions requiring review.</p>
              </div>
              ${statusChip(`${alerts.length} item${alerts.length === 1 ? "" : "s"}`, alerts.every((item) => item.positive) ? "success" : "warning")}
            </div>
            <div class="alert-stack">
              ${alerts.map((alert) => `
                <button class="command-alert" type="button" data-page="${alert.page}">
                  <span class="command-alert-icon">${escapeHtml(alert.icon)}</span>
                  <span>
                    <strong>${escapeHtml(alert.title)}</strong>
                    <small>${escapeHtml(alert.note)}</small>
                  </span>
                  <span aria-hidden="true">→</span>
                </button>
              `).join("")}
            </div>
          </article>
        </section>

        <section class="panel short-glass-panel">
          <div class="command-section-heading">
            <div>
              <h3>Short-glass liquid monitor</h3>
              <p>Actual quantity dispensed today and the current sellable balance.</p>
            </div>
            ${statusChip(shortGlassResult.ok ? "Live branch data" : "Report unavailable", shortGlassResult.ok ? "success" : "warning")}
          </div>
          <div class="short-glass-list">
            ${shortGlassRows.length ? shortGlassRows.map((row) => `
              <article class="short-glass-row">
                <div>
                  <strong>${escapeHtml(row.productName)}</strong>
                  <small>${escapeHtml(row.sku)} · ${formatNumber(row.glassSizeMl)} ml per glass</small>
                </div>
                <div><span>Glasses sold</span><strong>${formatNumber(row.glassesSold)}</strong></div>
                <div><span>Dispensed</span><strong>${formatNumber(row.volumeDispensedMl)} ml</strong></div>
                <div><span>Sales value</span><strong>${money(row.revenueMinor)}</strong></div>
                <div><span>Remaining</span><strong>${formatNumber(row.remainingGlasses)} glasses</strong></div>
              </article>
            `).join("") : `
              <div class="module-empty">
                ${shortGlassResult.ok
                  ? "No active short-glass product is configured for this branch."
                  : "The short-glass report could not be loaded. Other POS operations remain available."}
              </div>
            `}
          </div>
        </section>
      </div>
    `;
  }

  const moduleDefinitions = {
    accounting: {
      mark: "GL",
      eyebrow: "Finance control",
      title: "Accounting command view",
      description: "Review ledger readiness, journals and the current trial balance without changing posted accounting records.",
      requests: [
        ["trial", "/api/v3/reports/trial-balance?scope=shop"],
        ["journals", "/api/v3/accounting/journals?scope=shop&limit=10"],
        ["accounts", "/api/v3/accounting/accounts"]
      ],
      metrics(data) {
        const trial = data.trial || {};
        const journals = data.journals || {};
        const accounts = data.accounts || {};
        const debit = firstNumber(trial, ["totalDebitMinor", "totalDebitsMinor", "debitTotalMinor"]);
        const credit = firstNumber(trial, ["totalCreditMinor", "totalCreditsMinor", "creditTotalMinor"]);
        return [
          ["Total debits", money(debit), "Current trial balance"],
          ["Total credits", money(credit), "Current trial balance"],
          ["Recent journals", formatNumber(journals.count), debit === credit ? "Ledger balanced" : "Review imbalance"]
        ];
      },
      rows(data) {
        return (data.journals?.journals || []).map((item) => ({
          title: item.journalNumber || item.reference || item.id,
          note: `${item.description || item.sourceType || "Journal"} · ${formatDateTime(item.journalDate || item.createdAtUtc)}`,
          value: money(item.totalDebitMinor || item.totalMinor || 0),
          status: item.status || "posted"
        }));
      },
      actions: [["reports", "Open reports"], ["finance", "Receivables & cash"], ["settings", "Settings & backup"]]
    },
    finance: {
      mark: "FC",
      eyebrow: "Cash and settlement",
      title: "Receivables, payables and cash",
      description: "See outstanding customer balances, supplier obligations and recent ledger-derived cash movement.",
      requests: [
        ["receivables", "/api/v3/finance/receivables?status=open&limit=20"],
        ["payables", "/api/v3/finance/payables?status=open&limit=20"],
        ["cashbook", "/api/v3/finance/cashbook?scope=shop&limit=20"]
      ],
      metrics(data) {
        return [
          ["Customer debt", money(data.receivables?.outstandingMinor), `${formatNumber(data.receivables?.count)} open items`],
          ["Supplier obligations", money(data.payables?.outstandingMinor), `${formatNumber(data.payables?.count)} open items`],
          ["Cash movement", money(data.cashbook?.netMovementMinor), `${formatNumber(data.cashbook?.count)} recent entries`]
        ];
      },
      rows(data) {
        return (data.receivables?.receivables || []).slice(0, 10).map((item) => ({
          title: item.customerName || item.referenceNumber || item.id,
          note: `Due ${item.dueDate || "not set"}`,
          value: money(item.outstandingAmountMinor),
          status: item.status || "open"
        }));
      },
      actions: [["crm", "Open customer CRM"], ["accounting", "Open accounting"], ["reports", "Open reports"]]
    },
    procurement: {
      mark: "PO",
      eyebrow: "Supply chain",
      title: "Procurement and replenishment",
      description: "Monitor open purchase orders, goods received and branch-specific reorder recommendations.",
      requests: [
        ["orders", "/api/v3/procurement/purchase-orders?limit=20"],
        ["receipts", "/api/v3/procurement/goods-receipts?limit=20"],
        ["reorder", "/api/v3/procurement/reorder-recommendations"]
      ],
      metrics(data) {
        return [
          ["Open order value", money(data.orders?.openValueMinor), `${formatNumber(data.orders?.count)} purchase orders`],
          ["Received value", money(data.receipts?.totalMinor), `${formatNumber(data.receipts?.count)} goods receipts`],
          ["Reorder lines", formatNumber(data.reorder?.count), `${formatNumber(data.reorder?.suggestedQuantityBaseUnits)} base units suggested`]
        ];
      },
      rows(data) {
        return (data.orders?.purchaseOrders || []).slice(0, 10).map((item) => ({
          title: item.purchaseOrderNumber || item.id,
          note: `${item.supplierName || "Supplier"} · ${item.orderDate || "No date"}`,
          value: money(item.totalMinor),
          status: item.status
        }));
      },
      actions: [["purchases", "Direct purchase"], ["suppliers", "Supplier directory"], ["inventory", "Inventory"]]
    },
    crm: {
      mark: "CRM",
      eyebrow: "Customer growth",
      title: "Customer engagement command view",
      description: "Track customer value, follow-up workload, quotations and overdue actions from one place.",
      requests: [
        ["dashboard", "/api/v3/crm/dashboard"],
        ["tasks", "/api/v3/crm/tasks?limit=20"],
        ["quotations", "/api/v3/crm/quotations?limit=20"]
      ],
      metrics(data) {
        const dashboard = data.dashboard || {};
        return [
          ["Active customers", formatNumber(firstNumber(dashboard, ["activeCustomerCount", "customerCount"])), "Current customer base"],
          ["Quotation pipeline", money(data.quotations?.pipelineValueMinor), `${formatNumber(data.quotations?.count)} quotations`],
          ["Overdue follow-ups", formatNumber(data.tasks?.overdueCount), `${formatNumber(data.tasks?.count)} active tasks`]
        ];
      },
      rows(data) {
        return (data.tasks?.tasks || []).slice(0, 10).map((item) => ({
          title: item.title || item.subject || "Customer follow-up",
          note: `${item.customerName || "Customer"} · due ${item.dueAtUtc ? formatDateTime(item.dueAtUtc) : "not set"}`,
          value: item.priority || "normal",
          status: item.isOverdue ? "overdue" : (item.status || "open")
        }));
      },
      actions: [["sales", "New sale"], ["finance", "Customer balances"], ["receipts", "Receipts"]]
    },
    hrm: {
      mark: "HR",
      eyebrow: "Workforce operations",
      title: "People and workforce readiness",
      description: "Review active employees, attendance, leave, schedules and payroll preparation without mixing HR approval with accounting payments.",
      requests: [
        ["dashboard", "/api/v3/hrm/dashboard"],
        ["employees", "/api/v3/hrm/employees?includeAllShops=true&limit=20"],
        ["leave", "/api/v3/hrm/leave-requests?limit=20"]
      ],
      metrics(data) {
        const dashboard = data.dashboard || {};
        return [
          ["Active employees", formatNumber(firstNumber(dashboard, ["activeEmployeeCount"])), `${formatNumber(data.employees?.count)} visible records`],
          ["Today’s attendance", formatNumber(firstNumber(dashboard, ["todayAttendanceCount"])), "Clocked or approved today"],
          ["Pending leave", formatNumber(firstNumber(dashboard, ["pendingLeaveRequestCount", "pendingLeaveCount"])), "Awaiting manager action"]
        ];
      },
      rows(data) {
        return (data.employees?.employees || []).slice(0, 10).map((item) => ({
          title: item.displayName || `${item.firstName || ""} ${item.lastName || ""}`.trim() || item.employeeNumber,
          note: `${item.employeeNumber || "Employee"} · ${item.positionTitle || item.departmentName || "Workforce"}`,
          value: item.homeShopCode || "",
          status: item.status || "active"
        }));
      },
      actions: [["users", "User accounts"], ["settings", "Settings & backup"], ["dashboard", "Command centre"]]
    },
    saas: {
      mark: "SA",
      eyebrow: "Platform operations",
      title: "Tenant subscription and service health",
      description: "Review the organisation’s plan, enabled entitlements, usage snapshots and support access controls.",
      requests: [
        ["subscription", "/api/v3/saas/tenant/subscription"],
        ["entitlements", "/api/v3/saas/tenant/entitlements"],
        ["usage", "/api/v3/saas/tenant/usage-snapshots"]
      ],
      metrics(data) {
        const subscription = data.subscription || {};
        const latestUsage = data.usage?.snapshots?.[0] || {};
        return [
          ["Plan", firstText(subscription, ["planName", "planCode"], "Enterprise"), firstText(subscription, ["status"], "Active")],
          ["Active shops", formatNumber(latestUsage.activeShopCount), `${formatNumber(data.entitlements?.count)} entitlements`],
          ["Active users", formatNumber(latestUsage.activeUserCount), firstText(subscription, ["enforcementMode"], "Report only")]
        ];
      },
      rows(data) {
        return (data.entitlements?.entitlements || []).slice(0, 12).map((item) => ({
          title: item.key || item.entitlementKey,
          note: item.limitValue === null || item.limitValue === undefined ? "No numeric limit" : `Limit: ${formatNumber(item.limitValue)}`,
          value: item.isEnabled ? "Enabled" : "Disabled",
          status: item.isEnabled ? "enabled" : "inactive"
        }));
      },
      actions: [["settings", "Settings & backup"], ["users", "User accounts"], ["dashboard", "Command centre"]]
    }
  };

  async function renderModuleHub(moduleId) {
    const definition = moduleDefinitions[moduleId];
    const results = await Promise.all(definition.requests.map(async ([key, path]) => {
      const result = await safeApi(path);
      return [key, result];
    }));

    const data = {};
    const errors = [];
    for (const [key, result] of results) {
      if (result.ok) {
        data[key] = result.data;
      } else {
        errors.push(result.error?.message || `${key} unavailable`);
      }
    }

    const metrics = definition.metrics(data);
    const rows = definition.rows(data);

    $("#page").innerHTML = `
      <div>
        <section class="module-hero">
          <div>
            <span class="command-eyebrow" style="color:var(--nx-primary)">${escapeHtml(definition.eyebrow)}</span>
            <h2>${escapeHtml(definition.title)}</h2>
            <p>${escapeHtml(definition.description)}</p>
          </div>
          <div class="module-mark" aria-hidden="true">${escapeHtml(definition.mark)}</div>
        </section>

        <section class="module-metrics">
          ${metrics.map(([label, value, note], index) => metricCard(label, value, note, index === 0 ? "success" : "blue")).join("")}
        </section>

        <section class="module-layout">
          <article class="panel">
            <div class="command-section-heading">
              <div>
                <h3>Current records</h3>
                <p>Latest operational information from this branch.</p>
              </div>
              ${statusChip(errors.length ? "Partial data" : "Live data", errors.length ? "warning" : "success")}
            </div>
            <div class="module-list">
              ${rows.length ? rows.map((row) => `
                <article class="module-row">
                  <span class="nav-icon">${escapeHtml(definition.mark)}</span>
                  <div>
                    <strong>${escapeHtml(row.title || "Record")}</strong>
                    <small>${escapeHtml(row.note || "")}</small>
                  </div>
                  <div style="text-align:right">
                    <strong>${escapeHtml(row.value || "")}</strong>
                    <small>${statusChip(row.status || "current")}</small>
                  </div>
                </article>
              `).join("") : `<div class="module-empty">No current record was returned for this module.</div>`}
            </div>
          </article>

          <aside class="panel">
            <div class="command-section-heading">
              <div>
                <h3>Related workflows</h3>
                <p>Continue into connected operations.</p>
              </div>
            </div>
            <div class="alert-stack">
              ${definition.actions.map(([page, label]) => `
                <button class="command-alert" type="button" data-page="${page}">
                  <span class="command-alert-icon">→</span>
                  <span><strong>${escapeHtml(label)}</strong><small>${escapeHtml(pageMeta[page]?.[1] || "Open module")}</small></span>
                  <span aria-hidden="true">→</span>
                </button>
              `).join("")}
            </div>
            ${errors.length ? `
              <div class="module-empty" style="margin-top:14px;text-align:left">
                <strong>Some optional data was unavailable.</strong>
                <div style="margin-top:6px">${escapeHtml(errors.join(" · "))}</div>
              </div>
            ` : ""}
          </aside>
        </section>
      </div>
    `;
  }

  function renderCommandResults(query = "") {
    const host = $("#commandResults");
    if (!host) {
      return;
    }

    const normalized = query.trim().toLowerCase();
    const results = allPages().filter((page) => {
      return !normalized || `${page.label} ${page.subtitle} ${page.id}`.toLowerCase().includes(normalized);
    });

    host.innerHTML = results.length ? results.map((page, index) => `
      <button class="command-result" type="button" data-command-page="${page.id}">
        <span class="nav-icon">${escapeHtml(page.icon)}</span>
        <span><strong>${escapeHtml(page.label)}</strong><br><small>${escapeHtml(page.subtitle)}</small></span>
        <span class="command-result-key">${index < 9 ? `Alt+${index + 1}` : "Open"}</span>
      </button>
    `).join("") : `<div class="module-empty">No module matches “${escapeHtml(query)}”.</div>`;
  }

  function openCommandPalette() {
    const dialog = $("#commandPalette");
    if (!dialog) {
      return;
    }
    renderCommandResults();
    dialog.showModal();
    ui.commandOpen = true;
    document.body.classList.add("nexus-no-scroll");
    requestAnimationFrame(() => $("#commandSearch")?.focus());
  }

  function closeCommandPalette() {
    const dialog = $("#commandPalette");
    if (dialog?.open) {
      dialog.close();
    }
    ui.commandOpen = false;
    document.body.classList.remove("nexus-no-scroll");
  }

  function openMobileSidebar() {
    $("#application")?.classList.add("sidebar-open");
    document.body.classList.add("nexus-no-scroll");
  }

  function closeMobileSidebar() {
    $("#application")?.classList.remove("sidebar-open");
    if (!ui.commandOpen) {
      document.body.classList.remove("nexus-no-scroll");
    }
  }

  function updateNetworkStatus() {
    const host = $("#networkStatus");
    if (!host) {
      return;
    }
    const online = navigator.onLine;
    host.className = `network-pill ${online ? "online" : "offline"}`;
    host.innerHTML = `<span class="network-dot" aria-hidden="true"></span><span>${online ? "Connected" : "Offline"}</span>`;
    host.setAttribute("aria-label", online ? "Network connected" : "Network offline");
  }

  function enhanceAccountCard(user) {
    const account = document.querySelector(".account");
    if (!account) {
      return;
    }

    const initials = String(user.displayName || user.username || "NX")
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((part) => part[0])
      .join("")
      .toUpperCase();

    const logout = $("#logoutButton");
    account.innerHTML = `
      <div class="account-profile">
        <span class="account-avatar" aria-hidden="true">${escapeHtml(initials)}</span>
        <div class="account-copy">
          <strong id="userName">${escapeHtml(user.displayName)}</strong>
          <span id="userRole">${escapeHtml(user.role)}</span>
        </div>
      </div>
    `;
    account.appendChild(logout);
  }

  function enterEnterpriseApplication(user) {
    legacy.enterApplication(user);
    enhanceAccountCard(user);
    refreshShellContext().catch(handleError);
    const requested = location.hash.replace(/^#/, "");
    if (requested && findPage(requested)) {
      openEnterprisePage(requested);
    }
  }

  function showEnterpriseLogin(message = "") {
    closeCommandPalette();
    closeMobileSidebar();
    ui.context = null;
    legacy.showLogin(message);
  }

  function installEventHandlers() {
    $("#mobileMenuButton")?.addEventListener("click", openMobileSidebar);
    $("#sidebarClose")?.addEventListener("click", closeMobileSidebar);
    $("#sidebarBackdrop")?.addEventListener("click", closeMobileSidebar);

    $("#sidebarCollapse")?.addEventListener("click", () => {
      const application = $("#application");
      application.classList.toggle("sidebar-collapsed");
      localStorage.setItem("nexus.sidebarCollapsed", application.classList.contains("sidebar-collapsed") ? "1" : "0");
    });

    $("#navSearch")?.addEventListener("input", (event) => {
      ui.navigationFilter = event.target.value;
      renderEnterpriseNavigation();
    });

    $("#globalCommand")?.addEventListener("click", openCommandPalette);
    $("#commandClose")?.addEventListener("click", closeCommandPalette);
    $("#commandSearch")?.addEventListener("input", (event) => renderCommandResults(event.target.value));
    $("#commandPalette")?.addEventListener("close", () => {
      ui.commandOpen = false;
      document.body.classList.remove("nexus-no-scroll");
    });

    document.addEventListener("click", (event) => {
      const command = event.target.closest("[data-command-page]");
      if (command) {
        closeCommandPalette();
        openEnterprisePage(command.dataset.commandPage);
      }
    });

    document.addEventListener("keydown", (event) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        openCommandPalette();
      }

      if (event.altKey && /^[1-9]$/.test(event.key)) {
        const page = allPages()[Number(event.key) - 1];
        if (page) {
          event.preventDefault();
          openEnterprisePage(page.id);
        }
      }

      if (event.key === "Escape") {
        closeMobileSidebar();
      }
    });

    window.addEventListener("online", updateNetworkStatus);
    window.addEventListener("offline", updateNetworkStatus);
    window.addEventListener("hashchange", () => {
      const requested = location.hash.replace(/^#/, "");
      if (requested && requested !== ui.currentPage && findPage(requested)) {
        openEnterprisePage(requested);
      }
    });

    updateNetworkStatus();

    if (localStorage.getItem("nexus.sidebarCollapsed") === "1" && window.innerWidth > 900) {
      $("#application")?.classList.add("sidebar-collapsed");
    }
  }

  window.renderNavigation = renderEnterpriseNavigation;
  window.openPage = openEnterprisePage;
  window.renderDashboard = renderCommandCentre;
  window.enterApplication = enterEnterpriseApplication;
  window.showLogin = showEnterpriseLogin;

  renderNavigation = renderEnterpriseNavigation;
  openPage = openEnterprisePage;
  renderDashboard = renderCommandCentre;
  enterApplication = enterEnterpriseApplication;
  showLogin = showEnterpriseLogin;

  installEventHandlers();

  if (state.user) {
    enhanceAccountCard(state.user);
    renderEnterpriseNavigation();
    refreshShellContext().catch(handleError);
    openEnterprisePage(ui.currentPage);
  }
})();
