"use strict";

(function installPeopleFinanceWorkspaces() {
  const ws = {
    crmTab: "customers",
    financeTab: "receivables",
    hrmTab: "employees",
    financeSelection: null,
    financeRows: [],
    rendering: false,
    timer: null
  };
  const managed = new Set(["crm", "finance", "hrm"]);

  const current = () => {
    const value = location.hash.replace(/^#/, "");
    return managed.has(value) ? value : null;
  };
  const esc = (value) => typeof window.escapeHtml === "function"
    ? window.escapeHtml(String(value ?? ""))
    : String(value ?? "").replaceAll("&", "&amp;").replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;").replaceAll('"', "&quot;").replaceAll("'", "&#039;");
  const num = (value, digits = 0) => Number(value || 0).toLocaleString("en-UG", { maximumFractionDigits: digits });
  const money = (value) => `${num(value)} UGX`;
  const today = () => new Date().toISOString().slice(0, 10);
  const futureLocal = () => {
    const date = new Date(Date.now() + 86400000);
    return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
  };
  const dateTime = (value) => {
    if (!value) return "Not recorded";
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? String(value) : parsed.toLocaleString("en-UG", { dateStyle: "medium", timeStyle: "short" });
  };
  const rows = (source, keys) => {
    for (const key of keys) if (Array.isArray(source?.[key])) return source[key];
    return Array.isArray(source) ? source : [];
  };
  async function safe(path, options) {
    try { return { ok: true, data: await api(path, options) }; }
    catch (error) { return { ok: false, data: {}, error }; }
  }
  function notify(message, error = false) {
    const host = document.querySelector("#message");
    if (!host) return;
    host.textContent = message;
    host.classList.remove("hidden");
    host.classList.toggle("error", error);
    clearTimeout(notify.timer);
    notify.timer = setTimeout(() => host.classList.add("hidden"), 4500);
  }
  function status(value) {
    const text = String(value || "unknown").replaceAll("_", " ");
    const normalized = text.toLowerCase();
    const tone = ["active", "open", "approved", "completed", "posted", "accepted"].includes(normalized)
      ? "success"
      : ["overdue", "rejected", "cancelled", "reversed", "suspended"].includes(normalized)
        ? "danger" : "warning";
    return `<span class="pfh-status ${tone}">${esc(text)}</span>`;
  }
  const metric = (label, value, note) => `<article><span>${esc(label)}</span><strong>${esc(value)}</strong><small>${esc(note || "")}</small></article>`;
  const empty = (title, note) => `<div class="workspace-empty"><strong>${esc(title)}</strong><span>${esc(note)}</span></div>`;
  function tabs(module, items, active) {
    return `<div class="pfh-tabs" role="tablist" aria-label="${esc(module)} workspace views">${items.map(([id, label]) =>
      `<button type="button" role="tab" data-pfh-tab="${id}" data-pfh-module="${module}" aria-selected="${active === id}">${esc(label)}</button>`
    ).join("")}</div>`;
  }

  async function crmData() {
    const [dashboard, customers, tasks, quotations] = await Promise.all([
      safe("/api/v3/crm/dashboard"),
      safe("/api/v3/crm/customers?limit=100"),
      safe("/api/v3/crm/tasks?limit=100"),
      safe("/api/v3/crm/quotations?limit=100")
    ]);
    return {
      dashboard: dashboard.data || {},
      customers: rows(customers.data, ["customers"]),
      tasks: rows(tasks.data, ["tasks"]),
      quotations: rows(quotations.data, ["quotations"])
    };
  }

  function crmCustomers(data) {
    return `<section class="pfh-grid">
      <article class="panel pfh-table-panel">
        <div class="pfh-section-head"><div><h2>Customer profiles</h2><p>Contact, value, debt, loyalty and follow-up visibility.</p></div><span>${num(data.customers.length)} profiles</span></div>
        <div class="pfh-list">${data.customers.length ? data.customers.map((c) => `<article class="pfh-row">
          <div class="pfh-row-main"><strong>${esc(c.name)}</strong><small>${esc(c.customerNumber)} · ${esc(c.phone || c.email || "No contact recorded")}</small><div class="pfh-row-tags">${status(c.lifecycleStage)} ${c.loyaltyEnrolled ? status(c.loyaltyTier || "loyalty") : ""}</div></div>
          <div><span>Lifetime spend</span><strong>${money(c.metrics?.lifetimeSpendMinor)}</strong></div>
          <div><span>Outstanding</span><strong>${money(c.metrics?.outstandingMinor)}</strong></div>
          <div><span>Next follow-up</span><strong>${esc(c.nextFollowUpAtUtc ? dateTime(c.nextFollowUpAtUtc) : "Not scheduled")}</strong></div>
        </article>`).join("") : empty("No CRM customers", "Create the first profile using the form.")}</div>
      </article>
      <aside class="panel pfh-action-panel">
        <div class="pfh-section-head"><div><h2>Add customer</h2><p>Creates an organisation-owned CRM and finance customer.</p></div></div>
        <form id="pfhCustomerForm" class="pfh-form">
          <label>Customer name<input name="name" required maxlength="160"></label>
          <div class="pfh-form-two"><label>Phone<input name="phone" autocomplete="tel"></label><label>Email<input name="email" type="email" autocomplete="email"></label></div>
          <div class="pfh-form-two"><label>Lifecycle<select name="lifecycleStage"><option value="prospect">Prospect</option><option value="lead">Lead</option><option value="customer">Customer</option></select></label><label>Channel<select name="preferredChannel"><option value="phone">Phone</option><option value="whatsapp">WhatsApp</option><option value="email">Email</option></select></label></div>
          <label>Credit limit (UGX)<input name="creditLimitMinor" type="number" min="0" value="0"></label>
          <label>Notes<textarea name="notes" rows="3"></textarea></label>
          <button class="primary full" type="submit">Create customer profile</button>
        </form>
      </aside>
    </section>`;
  }

  function crmTasks(data) {
    const options = data.customers.map((c) => `<option value="${esc(c.id)}">${esc(c.name)} · ${esc(c.customerNumber)}</option>`).join("");
    return `<section class="pfh-grid">
      <article class="panel pfh-table-panel">
        <div class="pfh-section-head"><div><h2>Follow-up queue</h2><p>Priorities, due dates and completion controls.</p></div><span>${num(data.tasks.length)} tasks</span></div>
        <div class="pfh-list">${data.tasks.length ? data.tasks.map((t) => `<article class="pfh-row">
          <div class="pfh-row-main"><strong>${esc(t.title)}</strong><small>${esc(t.customerName || "General task")} · due ${esc(dateTime(t.dueAtUtc))}</small><div class="pfh-row-tags">${status(t.status)} ${status(t.priority)}</div></div>
          <div><span>Assigned to</span><strong>${esc(t.assignedToName || "Current user")}</strong></div>
          <div><span>Created by</span><strong>${esc(t.createdByName || "Nexus user")}</strong></div>
          <div>${t.status === "open" ? `<button type="button" data-complete-crm-task="${esc(t.id)}" data-version="${esc(t.version)}">Complete</button>` : ""}</div>
        </article>`).join("") : empty("No follow-ups", "Schedule a customer follow-up using the form.")}</div>
      </article>
      <aside class="panel pfh-action-panel">
        <div class="pfh-section-head"><div><h2>Schedule follow-up</h2><p>Adds a branch-scoped task with a deadline.</p></div></div>
        <form id="pfhTaskForm" class="pfh-form">
          <label>Customer<select name="customerId"><option value="">General business task</option>${options}</select></label>
          <label>Task title<input name="title" required maxlength="160"></label>
          <div class="pfh-form-two"><label>Priority<select name="priority"><option value="normal">Normal</option><option value="high">High</option><option value="urgent">Urgent</option></select></label><label>Due date<input name="dueAt" type="datetime-local" value="${futureLocal()}" required></label></div>
          <label>Details<textarea name="details" rows="4"></textarea></label>
          <button class="primary full" type="submit">Schedule follow-up</button>
        </form>
      </aside>
    </section>`;
  }

  function crmQuotes(data) {
    const pipeline = data.quotations.filter((q) => ["draft", "sent", "accepted"].includes(q.status)).reduce((sum, q) => sum + Number(q.totalMinor || 0), 0);
    return `<section class="panel pfh-table-panel">
      <div class="pfh-section-head"><div><h2>Quotation pipeline</h2><p>Draft, sent, accepted and converted proposals.</p></div><span>${money(pipeline)}</span></div>
      <div class="pfh-list">${data.quotations.length ? data.quotations.map((q) => `<article class="pfh-row">
        <div class="pfh-row-main"><strong>${esc(q.quotationNumber)}</strong><small>${esc(q.customerName)} · valid until ${esc(q.validUntil)}</small><div class="pfh-row-tags">${status(q.status)} ${q.isPastValidity ? status("overdue") : ""}</div></div>
        <div><span>Lines</span><strong>${num(q.lines?.length)}</strong></div><div><span>Total</span><strong>${money(q.totalMinor)}</strong></div><div><span>Created by</span><strong>${esc(q.createdByName || "Nexus user")}</strong></div>
      </article>`).join("") : empty("No quotations", "Create quotations through the existing CRM quotation API.")}</div>
    </section>`;
  }

  async function renderCrm() {
    const page = document.querySelector("#page");
    if (!page) return;
    page.innerHTML = `<div class="page-loading"><div class="skeleton"></div><div class="skeleton" style="min-height:340px"></div></div>`;
    const data = await crmData();
    const d = data.dashboard;
    page.dataset.pfhWorkspace = "crm";
    page.innerHTML = `<div class="pfh-workspace">
      <section class="pfh-hero crm"><div><span class="workspace-eyebrow">CUSTOMER OPERATIONS</span><h2>CRM transactional workspace</h2><p>Profiles, follow-ups, quotations, loyalty and debt visibility in one operational view.</p></div><button class="primary" type="button" data-page="sales">Start customer sale</button></section>
      <section class="pfh-metrics">${metric("Active customers", num(d.activeCustomerCount), `${num(d.newCustomerCount30Days)} new in 30 days`)}${metric("Customer debt", money(d.totalOutstandingMinor), `${num(d.debtorCustomerCount)} debtors`)}${metric("Overdue follow-ups", num(d.overdueTaskCount), `${num(d.openTaskCount)} open tasks`)}${metric("Quotation pipeline", money(d.openQuotationValueMinor), `${num(d.openQuotationCount)} open quotations`)}${metric("Loyalty members", num(d.loyaltyMemberCount), `${num(d.outstandingLoyaltyPoints)} points`)}</section>
      ${tabs("crm", [["customers", "Customers"], ["tasks", "Follow-ups"], ["quotations", "Quotations"]], ws.crmTab)}
      ${ws.crmTab === "tasks" ? crmTasks(data) : ws.crmTab === "quotations" ? crmQuotes(data) : crmCustomers(data)}
    </div>`;
  }

  async function financeData() {
    const [receivables, payables, cashbook, ageing] = await Promise.all([
      safe("/api/v3/finance/receivables?status=open&limit=100"),
      safe("/api/v3/finance/payables?status=open&limit=100"),
      safe("/api/v3/finance/cashbook?scope=shop&limit=100"),
      safe("/api/v3/reports/receivables-ageing?scope=shop")
    ]);
    return {
      receivables: rows(receivables.data, ["receivables"]),
      payables: rows(payables.data, ["payables"]),
      cashbook: rows(cashbook.data, ["entries"]),
      receivableOutstanding: Number(receivables.data?.outstandingMinor || 0),
      payableOutstanding: Number(payables.data?.outstandingMinor || 0),
      cashMovement: Number(cashbook.data?.netMovementMinor || 0),
      ageing: ageing.data || {}
    };
  }

  function mappedFinanceRows(data) {
    if (ws.financeTab === "cashbook") {
      return data.cashbook.map((item) => ({
        id: item.journalId,
        title: item.accountName || item.journalNumber,
        note: `${item.journalDate || ""} · ${item.journalDescription || item.lineDescription || ""}`,
        value: money(item.signedAmountMinor),
        status: item.direction || "posted",
        raw: item
      }));
    }
    const source = ws.financeTab === "payables" ? data.payables : data.receivables;
    return source.map((item) => ({
      id: item.id,
      title: ws.financeTab === "payables" ? item.supplierName : item.customerName,
      note: `${item.documentNumber || item.supplierInvoiceNumber || "Document"} · due ${item.dueDate || "not set"}`,
      value: money(item.outstandingAmountMinor),
      status: item.status || "open",
      raw: item
    }));
  }

  function settlementPanel() {
    const item = ws.financeSelection;
    if (!item || ws.financeTab === "cashbook") {
      return `<aside class="panel pfh-action-panel">${empty("Select an open item", "Choose a receivable or payable to prepare a controlled settlement.")}</aside>`;
    }
    const payable = ws.financeTab === "payables";
    const counterpartyId = payable ? item.supplierId : item.customerId;
    if (!counterpartyId) {
      return `<aside class="panel pfh-action-panel">${empty("Counterparty unavailable", "This legacy item has no linked counterparty and cannot be settled here.")}</aside>`;
    }
    return `<aside class="panel pfh-action-panel">
      <div class="pfh-section-head"><div><h2>${payable ? "Pay supplier" : "Receive customer payment"}</h2><p>${esc(payable ? item.supplierName : item.customerName)} · ${esc(item.documentNumber || item.supplierInvoiceNumber)}</p></div></div>
      <form id="pfhSettlementForm" class="pfh-form">
        <input type="hidden" name="itemId" value="${esc(item.id)}"><input type="hidden" name="counterpartyId" value="${esc(counterpartyId)}">
        <label>Settlement date<input name="date" type="date" value="${today()}" required></label>
        <label>Amount (UGX)<input name="amountMinor" type="number" min="1" max="${esc(item.outstandingAmountMinor)}" value="${esc(item.outstandingAmountMinor)}" required></label>
        <label>Payment method<select name="paymentMethod"><option value="cash">Cash</option><option value="mobile_money">Mobile money</option><option value="bank_transfer">Bank transfer</option><option value="card">Card</option></select></label>
        <label>Reference<input name="reference" maxlength="120"></label>
        <label>Notes<textarea name="notes" rows="3"></textarea></label>
        <button class="primary full" type="submit">${payable ? "Post supplier payment" : "Post customer receipt"}</button>
      </form>
    </aside>`;
  }

  async function renderFinance() {
    const page = document.querySelector("#page");
    if (!page) return;
    page.innerHTML = `<div class="page-loading"><div class="skeleton"></div><div class="skeleton" style="min-height:340px"></div></div>`;
    const data = await financeData();
    const displayRows = mappedFinanceRows(data);
    ws.financeRows = displayRows;
    page.dataset.pfhWorkspace = "finance";
    page.innerHTML = `<div class="pfh-workspace">
      <section class="pfh-hero finance"><div><span class="workspace-eyebrow">CONTROLLED SETTLEMENTS</span><h2>Receivables, payables and cashbook</h2><p>Collect customer debt, settle supplier obligations and inspect posted cash movement without bypassing accounting.</p></div><button type="button" data-page="accounting">Open accounting</button></section>
      <section class="pfh-metrics">${metric("Customer debt", money(data.receivableOutstanding), `${num(data.receivables.length)} open items`)}${metric("Supplier obligations", money(data.payableOutstanding), `${num(data.payables.length)} open items`)}${metric("Cash movement", money(data.cashMovement), `${num(data.cashbook.length)} recent entries`)}${metric("Over-90-day debt", money((data.ageing.buckets || []).find((x) => x.bucket === "over_90")?.amountMinor), "Receivables ageing")}</section>
      ${tabs("finance", [["receivables", "Customer debt"], ["payables", "Supplier obligations"], ["cashbook", "Cashbook"]], ws.financeTab)}
      <section class="pfh-grid">
        <article class="panel pfh-table-panel">
          <div class="pfh-section-head"><div><h2>${ws.financeTab === "cashbook" ? "Posted cash movement" : ws.financeTab === "payables" ? "Open supplier obligations" : "Open customer receivables"}</h2><p>All figures remain ledger-derived and branch-scoped.</p></div><span>${num(displayRows.length)} records</span></div>
          <div class="pfh-list">${displayRows.length ? displayRows.map((item) => `<button type="button" class="pfh-row pfh-select-row ${ws.financeSelection?.id === item.id ? "selected" : ""}" data-finance-item="${esc(item.id)}">
            <div class="pfh-row-main"><strong>${esc(item.title)}</strong><small>${esc(item.note)}</small><div class="pfh-row-tags">${status(item.status)}</div></div>
            <div><span>Amount</span><strong>${esc(item.value)}</strong></div><div><span>Action</span><strong>${ws.financeTab === "cashbook" ? "Inspect" : "Settle"}</strong></div>
          </button>`).join("") : empty("No records", "There are no matching finance items for this branch.")}</div>
        </article>
        ${settlementPanel()}
      </section>
    </div>`;
  }

  async function hrmData() {
    const day = today();
    const [dashboard, employees, attendance, leaveRequests, leaveTypes, payroll] = await Promise.all([
      safe("/api/v3/hrm/dashboard"),
      safe("/api/v3/hrm/employees?includeAllShops=true&limit=100"),
      safe(`/api/v3/hrm/attendance?fromDate=${day}&toDate=${day}`),
      safe("/api/v3/hrm/leave-requests"),
      safe("/api/v3/hrm/leave-types"),
      safe("/api/v3/hrm/payroll-periods?limit=50")
    ]);
    return {
      dashboard: dashboard.data || {},
      employees: rows(employees.data, ["employees"]),
      attendance: rows(attendance.data, ["attendance"]),
      leaveRequests: rows(leaveRequests.data, ["leaveRequests"]),
      leaveTypes: rows(leaveTypes.data, ["leaveTypes"]),
      payroll: rows(payroll.data, ["payrollPeriods"])
    };
  }

  const employeeOptions = (data) => data.employees.map((e) => `<option value="${esc(e.id)}">${esc(e.fullName)} · ${esc(e.employeeNumber)}</option>`).join("");

  function hrmEmployees(data) {
    return `<section class="panel pfh-table-panel">
      <div class="pfh-section-head"><div><h2>Employee profiles</h2><p>Role, branch, employment status and pay basis.</p></div><span>${num(data.employees.length)} employees</span></div>
      <div class="pfh-list">${data.employees.length ? data.employees.map((e) => `<article class="pfh-row">
        <div class="pfh-row-main"><strong>${esc(e.fullName)}</strong><small>${esc(e.employeeNumber)} · ${esc(e.positionTitle)} · ${esc(e.departmentName)}</small><div class="pfh-row-tags">${status(e.status)} ${status(e.employmentType)}</div></div>
        <div><span>Home branch</span><strong>${esc(e.homeShopName || e.homeShopCode)}</strong></div>
        <div><span>Base salary</span><strong>${money(e.baseSalaryMinor)}</strong></div>
        <div><span>Attendance</span><strong>${num(e.attendanceDayCount)} days</strong></div>
      </article>`).join("") : empty("No employees", "Create employee records through the existing HRM employee controls.")}</div>
    </section>`;
  }

  function hrmAttendance(data) {
    return `<section class="pfh-grid">
      <article class="panel pfh-table-panel">
        <div class="pfh-section-head"><div><h2>Today’s attendance</h2><p>Clock-in, clock-out and approval remain separate audited actions.</p></div><span>${num(data.attendance.length)} records</span></div>
        <div class="pfh-list">${data.attendance.length ? data.attendance.map((a) => `<article class="pfh-row">
          <div class="pfh-row-main"><strong>${esc(a.employeeName)}</strong><small>${esc(a.employeeNumber)} · ${esc(dateTime(a.clockInUtc))}</small><div class="pfh-row-tags">${status(a.status)}</div></div>
          <div><span>Worked</span><strong>${a.workedMinutes == null ? "In progress" : `${num(a.workedMinutes)} min`}</strong></div>
          <div><span>Overtime</span><strong>${num(a.overtimeMinutes)} min</strong></div>
          <div class="pfh-inline-actions">${!a.clockOutUtc ? `<button type="button" data-clock-out="${esc(a.id)}" data-version="${esc(a.version)}">Clock out</button>` : ""}${a.clockOutUtc && a.status !== "approved" ? `<button type="button" data-approve-attendance="${esc(a.id)}" data-version="${esc(a.version)}">Approve</button>` : ""}</div>
        </article>`).join("") : empty("No attendance today", "Clock in an employee using the form.")}</div>
      </article>
      <aside class="panel pfh-action-panel">
        <div class="pfh-section-head"><div><h2>Clock in employee</h2><p>Creates a manual attendance record for the active branch.</p></div></div>
        <form id="pfhClockInForm" class="pfh-form">
          <label>Employee<select name="employeeId" required><option value="">Select employee</option>${employeeOptions(data)}</select></label>
          <label>Notes<textarea name="notes" rows="3"></textarea></label>
          <button class="primary full" type="submit" ${data.employees.length ? "" : "disabled"}>Clock in now</button>
        </form>
      </aside>
    </section>`;
  }

  function hrmLeave(data) {
    const types = data.leaveTypes.map((t) => `<option value="${esc(t.id)}">${esc(t.name)} · ${num(t.annualEntitlementDays, 1)} days</option>`).join("");
    return `<section class="pfh-grid">
      <article class="panel pfh-table-panel">
        <div class="pfh-section-head"><div><h2>Leave requests</h2><p>Submission, approval and rejection preserve overlap validation.</p></div><span>${num(data.leaveRequests.length)} requests</span></div>
        <div class="pfh-list">${data.leaveRequests.length ? data.leaveRequests.map((r) => `<article class="pfh-row">
          <div class="pfh-row-main"><strong>${esc(r.employeeName)}</strong><small>${esc(r.leaveTypeName)} · ${esc(r.startDate)} to ${esc(r.endDate)}</small><div class="pfh-row-tags">${status(r.status)}</div></div>
          <div><span>Days</span><strong>${num(r.requestedDays, 1)}</strong></div><div><span>Reason</span><strong>${esc(r.reason || "Not specified")}</strong></div>
          <div class="pfh-inline-actions">${r.status === "submitted" ? `<button type="button" data-leave-decision="approve" data-request-id="${esc(r.id)}" data-version="${esc(r.version)}">Approve</button><button type="button" data-leave-decision="reject" data-request-id="${esc(r.id)}" data-version="${esc(r.version)}">Reject</button>` : ""}</div>
        </article>`).join("") : empty("No leave requests", "Create and submit the first request using the form.")}</div>
      </article>
      <aside class="panel pfh-action-panel">
        <div class="pfh-section-head"><div><h2>Request leave</h2><p>Creates and immediately submits a request for approval.</p></div></div>
        <form id="pfhLeaveForm" class="pfh-form">
          <label>Employee<select name="employeeId" required><option value="">Select employee</option>${employeeOptions(data)}</select></label>
          <label>Leave type<select name="leaveTypeId" required><option value="">Select leave type</option>${types}</select></label>
          <div class="pfh-form-two"><label>Start date<input name="startDate" type="date" value="${today()}" required></label><label>End date<input name="endDate" type="date" value="${today()}" required></label></div>
          <label>Requested days<input name="requestedDays" type="number" min="0.5" step="0.5" value="1" required></label>
          <label>Reason<textarea name="reason" rows="3"></textarea></label>
          <button class="primary full" type="submit" ${data.employees.length && data.leaveTypes.length ? "" : "disabled"}>Create and submit request</button>
        </form>
      </aside>
    </section>`;
  }

  function hrmPayroll(data) {
    return `<section class="pfh-grid">
      <article class="panel pfh-table-panel">
        <div class="pfh-section-head"><div><h2>Payroll periods</h2><p>Attendance-derived calculation and approval remain separate from payment.</p></div><span>${num(data.payroll.length)} periods</span></div>
        <div class="pfh-list">${data.payroll.length ? data.payroll.map((p) => `<article class="pfh-row">
          <div class="pfh-row-main"><strong>${esc(p.name)}</strong><small>${esc(p.startDate)} to ${esc(p.endDate)} · pay ${esc(p.payDate)}</small><div class="pfh-row-tags">${status(p.status)}</div></div>
          <div><span>Employees</span><strong>${num(p.employeeCount)}</strong></div><div><span>Net pay</span><strong>${money(p.netPayMinor)}</strong></div>
          <div class="pfh-inline-actions">${p.status === "draft" ? `<button type="button" data-payroll-action="calculate" data-period-id="${esc(p.id)}" data-version="${esc(p.version)}">Calculate</button>` : ""}${p.status === "calculated" ? `<button type="button" data-payroll-action="approve" data-period-id="${esc(p.id)}" data-version="${esc(p.version)}">Approve</button>` : ""}</div>
        </article>`).join("") : empty("No payroll periods", "Create a payroll period using the form.")}</div>
      </article>
      <aside class="panel pfh-action-panel">
        <div class="pfh-section-head"><div><h2>Create payroll period</h2><p>Defines the attendance window and scheduled pay date.</p></div></div>
        <form id="pfhPayrollForm" class="pfh-form">
          <label>Period name<input name="name" required value="Monthly payroll"></label>
          <div class="pfh-form-two"><label>Start date<input name="startDate" type="date" value="${today()}" required></label><label>End date<input name="endDate" type="date" value="${today()}" required></label></div>
          <label>Pay date<input name="payDate" type="date" value="${today()}" required></label>
          <button class="primary full" type="submit">Create payroll period</button>
        </form>
      </aside>
    </section>`;
  }

  async function renderHrm() {
    const page = document.querySelector("#page");
    if (!page) return;
    page.innerHTML = `<div class="page-loading"><div class="skeleton"></div><div class="skeleton" style="min-height:340px"></div></div>`;
    const data = await hrmData();
    const d = data.dashboard;
    page.dataset.pfhWorkspace = "hrm";
    page.innerHTML = `<div class="pfh-workspace">
      <section class="pfh-hero hrm"><div><span class="workspace-eyebrow">WORKFORCE OPERATIONS</span><h2>People, attendance, leave and payroll</h2><p>Operate the workforce from employee identity through approved time and payroll readiness.</p></div><button type="button" data-page="users">Manage login accounts</button></section>
      <section class="pfh-metrics">${metric("Active employees", num(d.activeEmployeeCount), `${num(d.probationEmployeeCount)} on probation`)}${metric("Attendance today", num(d.todayAttendanceCount), `${num(d.openAttendanceCount)} open records`)}${metric("Pending leave", num(d.pendingLeaveRequestCount), `${num(d.approvedLeaveTodayCount)} approved today`)}${metric("Payroll readiness", money(d.latestPayrollNetMinor), `${num(d.draftPayrollPeriodCount)} draft periods`)}${metric("Workforce alerts", num(Number(d.openDisciplinaryCaseCount || 0) + Number(d.expiringTrainingCount90Days || 0)), "Cases and expiring training")}</section>
      ${tabs("hrm", [["employees", "Employees"], ["attendance", "Attendance"], ["leave", "Leave"], ["payroll", "Payroll"]], ws.hrmTab)}
      ${ws.hrmTab === "attendance" ? hrmAttendance(data) : ws.hrmTab === "leave" ? hrmLeave(data) : ws.hrmTab === "payroll" ? hrmPayroll(data) : hrmEmployees(data)}
    </div>`;
  }

  async function renderCurrent(force = false) {
    const module = current();
    const page = document.querySelector("#page");
    if (!module || !page || ws.rendering) return;
    if (!force && page.dataset.pfhWorkspace === module && page.querySelector(".pfh-workspace")) return;
    ws.rendering = true;
    try {
      if (module === "crm") await renderCrm();
      if (module === "finance") await renderFinance();
      if (module === "hrm") await renderHrm();
    } catch (error) {
      notify(error.message || "The workspace could not be loaded.", true);
      if (typeof window.handleError === "function") window.handleError(error);
    } finally {
      ws.rendering = false;
    }
  }

  function schedule(force = false) {
    clearTimeout(ws.timer);
    ws.timer = setTimeout(() => renderCurrent(force), 25);
  }

  document.addEventListener("click", async (event) => {
    const tab = event.target.closest("[data-pfh-tab]");
    if (tab) {
      const module = tab.dataset.pfhModule;
      ws[`${module}Tab`] = tab.dataset.pfhTab;
      if (module === "finance") ws.financeSelection = null;
      schedule(true);
      return;
    }

    const financeItem = event.target.closest("[data-finance-item]");
    if (financeItem) {
      const row = ws.financeRows.find((item) => item.id === financeItem.dataset.financeItem);
      ws.financeSelection = row?.raw || null;
      await renderFinance();
      return;
    }

    const completeTask = event.target.closest("[data-complete-crm-task]");
    if (completeTask) {
      try {
        await api(`/api/v3/crm/tasks/${completeTask.dataset.completeCrmTask}/complete`, {
          method: "POST",
          body: JSON.stringify({ expectedVersion: Number(completeTask.dataset.version), completionNotes: "Completed from CRM workspace" })
        });
        notify("CRM follow-up completed.");
        await renderCrm();
      } catch (error) { notify(error.message || "Follow-up could not be completed.", true); }
      return;
    }

    const clockOut = event.target.closest("[data-clock-out]");
    if (clockOut) {
      try {
        await api(`/api/v3/hrm/attendance/${clockOut.dataset.clockOut}/clock-out`, {
          method: "POST",
          body: JSON.stringify({ expectedVersion: Number(clockOut.dataset.version), breakMinutes: 0, notes: "Clocked out from workforce workspace" })
        });
        notify("Employee clocked out.");
        await renderHrm();
      } catch (error) { notify(error.message || "Clock-out failed.", true); }
      return;
    }

    const approveAttendance = event.target.closest("[data-approve-attendance]");
    if (approveAttendance) {
      try {
        await api(`/api/v3/hrm/attendance/${approveAttendance.dataset.approveAttendance}/approve`, {
          method: "POST",
          body: JSON.stringify({ expectedVersion: Number(approveAttendance.dataset.version), notes: "Approved from workforce workspace" })
        });
        notify("Attendance approved.");
        await renderHrm();
      } catch (error) { notify(error.message || "Attendance approval failed.", true); }
      return;
    }

    const leaveDecision = event.target.closest("[data-leave-decision]");
    if (leaveDecision) {
      const action = leaveDecision.dataset.leaveDecision;
      try {
        await api(`/api/v3/hrm/leave-requests/${leaveDecision.dataset.requestId}/${action}`, {
          method: "POST",
          body: JSON.stringify({ expectedVersion: Number(leaveDecision.dataset.version), decisionNotes: `${action}d from workforce workspace` })
        });
        notify(`Leave request ${action}d.`);
        await renderHrm();
      } catch (error) { notify(error.message || "Leave decision failed.", true); }
      return;
    }

    const payrollAction = event.target.closest("[data-payroll-action]");
    if (payrollAction) {
      const action = payrollAction.dataset.payrollAction;
      const payload = action === "calculate"
        ? { expectedVersion: Number(payrollAction.dataset.version), defaultAllowanceMinor: 0, defaultDeductionMinor: 0, overtimeRateMinorPerHour: 0 }
        : { expectedVersion: Number(payrollAction.dataset.version) };
      try {
        await api(`/api/v3/hrm/payroll-periods/${payrollAction.dataset.periodId}/${action}`, { method: "POST", body: JSON.stringify(payload) });
        notify(`Payroll ${action} completed.`);
        await renderHrm();
      } catch (error) { notify(error.message || `Payroll ${action} failed.`, true); }
    }
  });

  document.addEventListener("submit", async (event) => {
    if (!event.target.matches("#pfhCustomerForm, #pfhTaskForm, #pfhSettlementForm, #pfhClockInForm, #pfhLeaveForm, #pfhPayrollForm")) return;
    event.preventDefault();
    const form = event.target;
    const values = Object.fromEntries(new FormData(form).entries());
    const button = form.querySelector('button[type="submit"]');
    if (button) button.disabled = true;

    try {
      if (form.id === "pfhCustomerForm") {
        await api("/api/v3/crm/customers", {
          method: "POST",
          body: JSON.stringify({
            name: values.name,
            phone: values.phone || null,
            email: values.email || null,
            lifecycleStage: values.lifecycleStage,
            preferredChannel: values.preferredChannel,
            creditLimitMinor: Number(values.creditLimitMinor || 0),
            loyaltyEnrolled: true,
            notes: values.notes || null
          })
        });
        notify("Customer profile created.");
        await renderCrm();
      }

      if (form.id === "pfhTaskForm") {
        await api("/api/v3/crm/tasks", {
          method: "POST",
          body: JSON.stringify({
            customerId: values.customerId || null,
            title: values.title,
            details: values.details || null,
            priority: values.priority,
            dueAtUtc: new Date(values.dueAt).toISOString()
          })
        });
        notify("Customer follow-up scheduled.");
        await renderCrm();
      }

      if (form.id === "pfhSettlementForm") {
        const payable = ws.financeTab === "payables";
        const body = {
          [payable ? "supplierId" : "customerId"]: values.counterpartyId,
          [payable ? "paymentDate" : "receiptDate"]: values.date,
          paymentMethod: values.paymentMethod,
          reference: values.reference || null,
          notes: values.notes || null,
          allocations: [{ itemId: values.itemId, amountMinor: Number(values.amountMinor) }]
        };
        await api(payable ? "/api/v3/finance/supplier-payments" : "/api/v3/finance/customer-receipts", {
          method: "POST",
          body: JSON.stringify(body)
        });
        ws.financeSelection = null;
        notify(payable ? "Supplier payment posted." : "Customer receipt posted.");
        await renderFinance();
      }

      if (form.id === "pfhClockInForm") {
        await api("/api/v3/hrm/attendance/clock-in", {
          method: "POST",
          body: JSON.stringify({ employeeId: values.employeeId, source: "manual", notes: values.notes || null })
        });
        notify("Employee clocked in.");
        await renderHrm();
      }

      if (form.id === "pfhLeaveForm") {
        const created = await api("/api/v3/hrm/leave-requests", {
          method: "POST",
          body: JSON.stringify({
            employeeId: values.employeeId,
            leaveTypeId: values.leaveTypeId,
            startDate: values.startDate,
            endDate: values.endDate,
            requestedDays: Number(values.requestedDays),
            reason: values.reason || null
          })
        });
        await api(`/api/v3/hrm/leave-requests/${created.id}/submit`, {
          method: "POST",
          body: JSON.stringify({ expectedVersion: Number(created.version), decisionNotes: "Submitted from workforce workspace" })
        });
        notify("Leave request created and submitted.");
        await renderHrm();
      }

      if (form.id === "pfhPayrollForm") {
        await api("/api/v3/hrm/payroll-periods", {
          method: "POST",
          body: JSON.stringify({ name: values.name, startDate: values.startDate, endDate: values.endDate, payDate: values.payDate })
        });
        notify("Payroll period created.");
        await renderHrm();
      }
    } catch (error) {
      notify(error.message || "The transaction could not be completed.", true);
    } finally {
      if (button) button.disabled = false;
    }
  });

  window.addEventListener("hashchange", () => schedule(false));
  new MutationObserver(() => {
    const module = current();
    const page = document.querySelector("#page");
    if (module && page && !ws.rendering && !page.querySelector(".pfh-workspace")) schedule(false);
  }).observe(document.documentElement, { childList: true, subtree: true });

  schedule(false);
})();