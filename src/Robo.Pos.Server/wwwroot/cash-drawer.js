"use strict";

(function installCashDrawerWorkspace() {
  const denominations = [50000, 20000, 10000, 5000, 2000, 1000, 500, 200, 100, 50];
  const state = {
    loading: false,
    drawer: null,
    drawerError: null,
    reviews: [],
    reviewStatus: "pending"
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
      return { ok: false, data: null, error };
    }
  }

  function kpi(label, value, note, tone = "") {
    return `<article class="cd-kpi ${tone}"><span>${esc(label)}</span><strong>${esc(value)}</strong><small>${esc(note)}</small></article>`;
  }

  function movementLabel(type) {
    return type === "float_in" ? "Float in" : "Safe drop";
  }

  function drawerSummary() {
    const drawer = state.drawer;
    if (!drawer) {
      return `<section class="panel cd-no-shift">
        <div><span class="workspace-eyebrow">DRAWER STATUS</span><h2>No open teller shift</h2><p>${esc(state.drawerError || "Open a shift before recording drawer movements or cash counts.")}</p></div>
        <button type="button" class="secondary" data-cash-refresh>Refresh drawer</button>
      </section>`;
    }

    return `<>
      <section class="cd-kpis" aria-label="Current cash drawer position">
        ${kpi("Opening cash", money(drawer.openingCashMinor), "Shift opening float")}
        ${kpi("Cash sales", money(drawer.cashSalesMinor), "Gross cash collected", "positive")}
        ${kpi("Cash refunds", money(drawer.cashRefundsMinor), "Completed cash refunds", "negative")}
        ${kpi("Float added", money(drawer.floatInMinor), "Internal custody transfer")}
        ${kpi("Safe drops", money(drawer.safeDropMinor), "Removed from teller custody")}
        ${kpi("Expected drawer cash", money(drawer.expectedDrawerCashMinor), `${drawer.shopCode} · ${drawer.shopName}`, "accent")}
      </section>

      <section class="cd-control-grid">
        <section class="panel cd-form-panel">
          <div class="cd-panel-head"><div><span class="workspace-eyebrow">CASH CUSTODY</span><h2>Record drawer movement</h2><p>Float and safe-drop entries move cash custody only. They do not create income, expenses or cashbook postings.</p></div></div>
          <form id="cashDrawerMovementForm" class="cd-form">
            <label><span>Movement type</span><select name="movementType" required><option value="safe_drop">Safe drop — remove from drawer</option><option value="float_in">Float in — add to drawer</option></select></label>
            <label><span>Amount (UGX)</span><input type="number" name="amountMinor" min="1" step="1" required></label>
            <label class="wide"><span>Reason</span><textarea name="reason" rows="2" minlength="3" maxlength="250" required placeholder="Why is cash moving into or out of teller custody?"></textarea></label>
            <label><span>Reference</span><input name="reference" maxlength="100" placeholder="Safe bag, voucher or approval reference"></label>
            <button class="primary" type="submit">Record drawer movement</button>
          </form>
        </section>

        <section class="panel cd-form-panel">
          <div class="cd-panel-head"><div><span class="workspace-eyebrow">PHYSICAL COUNT</span><h2>Count cash by denomination</h2><p>Record an interim check or the final count that will support shift reconciliation.</p></div><strong id="cashCountTotal">0 UGX</strong></div>
          <form id="cashDrawerCountForm" class="cd-count-form">
            <label><span>Count type</span><select name="countType"><option value="interim">Interim count</option><option value="closing">Closing count</option></select></label>
            <div class="cd-denominations" aria-label="Cash denominations">
              ${denominations.map((value) => `<label><span>${number(value)} UGX</span><input type="number" min="0" step="1" value="0" data-denomination="${value}" aria-label="Quantity of ${number(value)} Uganda shilling notes or coins"></label>`).join("")}
            </div>
            <label class="wide"><span>Count notes</span><textarea name="notes" rows="2" maxlength="500" placeholder="Optional seal, witness or discrepancy note"></textarea></label>
            <button class="primary" type="submit">Record denomination count</button>
          </form>
        </section>
      </section>
    </>`;
  }

  function movementHistory() {
    const movements = state.drawer?.movements || [];
    return `<section class="panel cd-history-panel">
      <div class="cd-panel-head"><div><h2>Drawer movement register</h2><p>Immutable float and safe-drop records for the current shift.</p></div><span>${number(movements.length)} records</span></div>
      <div class="cd-history-list">${movements.length ? movements.map((item) => `<article class="cd-history-row">
        <div><strong>${esc(item.movementNumber)}</strong><span>${esc(movementLabel(item.movementType))} · ${esc(item.reason)}</span><small>${esc(item.reference || "No reference")} · ${esc(dateTime(item.createdAtUtc))}</small></div>
        <div><strong class="${item.movementType === "float_in" ? "positive-text" : "negative-text"}">${item.movementType === "float_in" ? "+" : "−"}${money(item.amountMinor)}</strong><small>${esc(item.approvedByDisplayName)}</small></div>
      </article>`).join("") : '<div class="workspace-empty"><strong>No drawer movements</strong><span>Float and safe-drop records will appear here.</span></div>'}</div>
    </section>`;
  }

  function countHistory() {
    const counts = state.drawer?.counts || [];
    return `<section class="panel cd-history-panel">
      <div class="cd-panel-head"><div><h2>Cash count history</h2><p>Denomination snapshots recorded during the current shift.</p></div><span>${number(counts.length)} counts</span></div>
      <div class="cd-history-list">${counts.length ? counts.map((item) => `<article class="cd-history-row">
        <div><strong>${esc(item.countType === "closing" ? "Closing count" : "Interim count")}</strong><span>${esc(item.notes || "No count notes")}</span><small>${esc(item.countedByDisplayName)} · ${esc(dateTime(item.createdAtUtc))}</small></div>
        <div><strong>${money(item.totalMinor)}</strong><small>${number((item.denominations || []).filter((line) => Number(line.quantity) > 0).length)} denominations used</small></div>
      </article>`).join("") : '<div class="workspace-empty"><strong>No cash counts</strong><span>Interim and closing denomination counts will appear here.</span></div>'}</div>
    </section>`;
  }

  function reviewCard(review) {
    const pending = review.reviewStatus === "pending";
    const varianceClass = Number(review.varianceMinor) === 0 ? "balanced" : Number(review.varianceMinor) > 0 ? "over" : "short";
    return `<article class="cd-review-card ${varianceClass}">
      <div class="cd-review-main">
        <div><span class="cd-status ${esc(review.reviewStatus)}">${esc(review.reviewStatus)}</span><strong>${esc(review.tellerDisplayName)}</strong><small>${esc(review.shopCode)} · ${esc(dateTime(review.createdAtUtc))}</small></div>
        <div class="cd-review-money"><span>Expected <strong>${money(review.expectedCashMinor)}</strong></span><span>Counted <strong>${money(review.countedCashMinor)}</strong></span><span>Variance <strong>${money(review.varianceMinor)}</strong></span></div>
      </div>
      ${pending ? `<div class="cd-review-actions"><label><span>Manager review note</span><textarea rows="2" maxlength="500" data-review-notes="${esc(review.shiftId)}" placeholder="Explain approval or rejection"></textarea></label><div><button type="button" class="secondary" data-review-decision="rejected" data-shift-id="${esc(review.shiftId)}">Reject reconciliation</button><button type="button" class="primary" data-review-decision="approved" data-shift-id="${esc(review.shiftId)}">Approve reconciliation</button></div></div>` : `<div class="cd-reviewed"><span>${esc(review.reviewNotes || "No review note")}</span><small>${esc(review.reviewedByDisplayName || "Manager")} · ${esc(dateTime(review.reviewedAtUtc))}</small></div>`}
    </article>`;
  }

  function reviewsPanel() {
    return `<section class="panel cd-reviews-panel">
      <div class="cd-panel-head"><div><span class="workspace-eyebrow">MANAGER CONTROL</span><h2>Shift reconciliation queue</h2><p>Every closed shift produces one permanent review record.</p></div><label><span>Review status</span><select id="cashReviewStatus"><option value="pending" ${state.reviewStatus === "pending" ? "selected" : ""}>Pending</option><option value="approved" ${state.reviewStatus === "approved" ? "selected" : ""}>Approved</option><option value="rejected" ${state.reviewStatus === "rejected" ? "selected" : ""}>Rejected</option><option value="" ${state.reviewStatus === "" ? "selected" : ""}>All reviews</option></select></label></div>
      <div class="cd-review-list">${state.reviews.length ? state.reviews.map(reviewCard).join("") : '<div class="workspace-empty"><strong>No matching reconciliations</strong><span>Closed shifts matching this review status will appear here.</span></div>'}</div>
    </section>`;
  }

  function render() {
    const page = document.querySelector("#page");
    if (!page) return;
    page.dataset.cashDrawerWorkspace = "1";
    page.innerHTML = `<div class="cash-drawer-workspace">
      <header class="workspace-hero cd-hero"><div><span class="workspace-eyebrow">NEXUS POS 6.9 · CASH CONTROL</span><h1>Cash drawer and shift reconciliation</h1><p>Know what should be in the drawer, document every custody movement and require a permanent manager decision after closure.</p></div><div class="workspace-actions"><button type="button" class="secondary" data-cash-refresh>Refresh cash control</button></div></header>
      ${drawerSummary().replace(/^<>|<\/>$/g, "")}
      <section class="cd-history-grid">${movementHistory()}${countHistory()}</section>
      ${reviewsPanel()}
    </div>`;
    updateCountTotal();
  }

  async function load() {
    if (state.loading) return;
    state.loading = true;
    const page = document.querySelector("#page");
    if (page) page.innerHTML = '<div class="workspace-loading"><strong>Loading cash controls…</strong><span>Reading the active drawer and reconciliation queue.</span></div>';

    const [drawerResult, reviewResult] = await Promise.all([
      safe("/api/v3/cash-drawer/current"),
      safe(`/api/v3/admin/cash-drawer/reconciliations?status=${encodeURIComponent(state.reviewStatus)}`)
    ]);
    state.drawer = drawerResult.ok ? drawerResult.data : null;
    state.drawerError = drawerResult.ok ? null : (drawerResult.error?.message || "No open shift is available.");
    state.reviews = reviewResult.ok ? (Array.isArray(reviewResult.data) ? reviewResult.data : []) : [];
    state.loading = false;
    render();
    if (!reviewResult.ok) notify(reviewResult.error?.message || "Reconciliation reviews could not be loaded.", true);
  }

  function denominationLines() {
    return [...document.querySelectorAll("[data-denomination]")].map((input) => ({
      denominationMinor: Number(input.dataset.denomination),
      quantity: Number(input.value || 0)
    })).filter((line) => line.quantity > 0);
  }

  function updateCountTotal() {
    const total = denominationLines().reduce((sum, line) => sum + line.denominationMinor * line.quantity, 0);
    const host = document.querySelector("#cashCountTotal");
    if (host) host.textContent = money(total);
  }

  document.addEventListener("input", (event) => {
    if (event.target.matches("[data-denomination]")) updateCountTotal();
  });

  document.addEventListener("change", async (event) => {
    if (event.target.id !== "cashReviewStatus") return;
    state.reviewStatus = event.target.value;
    await load();
  });

  document.addEventListener("click", async (event) => {
    const refresh = event.target.closest("[data-cash-refresh]");
    if (refresh) {
      await load();
      return;
    }

    const reviewButton = event.target.closest("[data-review-decision]");
    if (!reviewButton) return;
    const shiftId = reviewButton.dataset.shiftId;
    const decision = reviewButton.dataset.reviewDecision;
    const notes = document.querySelector(`[data-review-notes="${CSS.escape(shiftId)}"]`)?.value || "";
    reviewButton.disabled = true;
    const result = await safe(`/api/v3/admin/cash-drawer/reconciliations/${encodeURIComponent(shiftId)}/review`, {
      method: "POST",
      body: JSON.stringify({ decision, notes })
    });
    if (!result.ok) {
      reviewButton.disabled = false;
      notify(result.error?.message || "The reconciliation could not be reviewed.", true);
      return;
    }
    notify(`Shift reconciliation ${decision}.`);
    await load();
  });

  document.addEventListener("submit", async (event) => {
    if (event.target.id === "cashDrawerMovementForm") {
      event.preventDefault();
      const values = Object.fromEntries(new FormData(event.target));
      const submit = event.target.querySelector('button[type="submit"]');
      submit.disabled = true;
      const result = await safe("/api/v3/cash-drawer/movements", {
        method: "POST",
        body: JSON.stringify({
          movementType: values.movementType,
          amountMinor: Number(values.amountMinor),
          reason: values.reason,
          reference: values.reference
        })
      });
      if (!result.ok) {
        submit.disabled = false;
        notify(result.error?.message || "The drawer movement could not be recorded.", true);
        return;
      }
      notify(`${result.data.movementNumber} recorded for ${money(result.data.amountMinor)}.`);
      event.target.reset();
      await load();
      return;
    }

    if (event.target.id === "cashDrawerCountForm") {
      event.preventDefault();
      const lines = denominationLines();
      if (!lines.length) {
        notify("Enter at least one denomination quantity.", true);
        return;
      }
      const values = Object.fromEntries(new FormData(event.target));
      const submit = event.target.querySelector('button[type="submit"]');
      submit.disabled = true;
      const result = await safe("/api/v3/cash-drawer/counts", {
        method: "POST",
        body: JSON.stringify({ countType: values.countType, denominations: lines, notes: values.notes })
      });
      if (!result.ok) {
        submit.disabled = false;
        notify(result.error?.message || "The denomination count could not be recorded.", true);
        return;
      }
      notify(`${result.data.countType === "closing" ? "Closing" : "Interim"} count recorded at ${money(result.data.totalMinor)}.`);
      event.target.reset();
      await load();
    }
  });

  window.NexusCashDrawer = {
    render: load,
    isRendering: () => state.loading
  };
})();
