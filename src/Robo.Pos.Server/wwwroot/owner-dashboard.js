"use strict";

const ownerDashboardUi = {
  periodDays: 1,
  products: [],
  refreshTimer: null,
  isLoading: false
};

function ownerLocalIsoDate(date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");

  return `${year}-${month}-${day}`;
}

function ownerReportRange(days) {
  const to = new Date();
  const from = new Date();

  from.setDate(to.getDate() - Math.max(0, days - 1));

  return {
    from: ownerLocalIsoDate(from),
    to: ownerLocalIsoDate(to)
  };
}

function ownerFormatQuantity(value, unit) {
  const quantity = Number(value || 0);

  const formatted = Number.isInteger(quantity)
    ? quantity.toLocaleString("en-US")
    : quantity.toLocaleString("en-US", {
        maximumFractionDigits: 2
      });

  return `${formatted} ${unit || "unit"}`;
}

function ownerFormatDateTime(value) {
  if (!value) {
    return "Not available";
  }

  return new Date(value).toLocaleString();
}

function ownerPeriodLabel(days) {
  if (days === 1) {
    return "Today";
  }

  return `Last ${days} days`;
}

function ownerStatusBadge(text, type = "active") {
  return `
    <span class="owner-status ${escapeHtml(type)}">
      ${escapeHtml(text)}
    </span>
  `;
}

function ownerMetric(title, value, note = "") {
  return `
    <article class="owner-metric">
      <span>${escapeHtml(title)}</span>
      <strong>${escapeHtml(String(value))}</strong>
      <small>${escapeHtml(note)}</small>
    </article>
  `;
}

function renderOwnerStockRows(searchText = "") {
  const host = $("#ownerStockRows");

  if (!host) {
    return;
  }

  const query = searchText.trim().toLowerCase();

  const products = ownerDashboardUi.products.filter((product) => {
    if (!query) {
      return true;
    }

    return [
      product.name,
      product.sku,
      product.categoryName,
      product.stockUnit
    ]
      .filter(Boolean)
      .join(" ")
      .toLowerCase()
      .includes(query);
  });

  host.innerHTML = products.length
    ? products.map((product) => `
        <article class="owner-stock-card">
          <div class="owner-stock-main">
            <div>
              <strong>${escapeHtml(product.name)}</strong>

              <small>
                ${escapeHtml(product.categoryName || "Uncategorised")}
                · ${escapeHtml(product.sku)}
              </small>
            </div>

            ${
              product.isLowStock
                ? ownerStatusBadge("Low stock", "warning")
                : ownerStatusBadge("Available", "active")
            }
          </div>

          <div class="owner-stock-values">
            <div>
              <span>Available</span>

              <strong>
                ${escapeHtml(
                  ownerFormatQuantity(
                    product.availableBaseUnits,
                    product.stockUnit
                  )
                )}
              </strong>
            </div>

            <div>
              <span>Selling price</span>
              <strong>${escapeHtml(money(product.sellingPriceMinor))}</strong>
            </div>
          </div>
        </article>
      `).join("")
    : `
      <div class="empty">
        No products match your search.
      </div>
    `;
}

async function renderOwnerDashboard(options = {}) {
  if (ownerDashboardUi.isLoading) {
    return;
  }

  ownerDashboardUi.isLoading = true;

  const page = $("#page");

  if (!options.silent) {
    page.innerHTML = `
      <section
        class="panel owner-dashboard-loading"
        data-owner-dashboard-root
      >
        <h2>Loading owner information…</h2>
        <p>Reading live sales and stock from the shop computer.</p>
      </section>
    `;
  }

  try {
    const range = ownerReportRange(
      ownerDashboardUi.periodDays
    );

    const query = new URLSearchParams({
      from: range.from,
      to: range.to
    });

    const [
      summary,
      report,
      inventoryResult
    ] = await Promise.all([
      api("/api/v3/admin/summary"),

      api(
        `/api/v3/admin/reports/summary?${query.toString()}`
      ),

      api("/api/v3/admin/inventory/products")
    ]);

    const products = (inventoryResult.products || [])
      .filter((product) => product.isActive)
      .sort((left, right) =>
        left.name.localeCompare(right.name)
      );

    const lowStock = products.filter(
      (product) => product.isLowStock
    );

    ownerDashboardUi.products = products;

    const refreshedAt = new Date();

    page.innerHTML = `
      <div
        class="owner-dashboard"
        data-owner-dashboard-root
      >
        <section class="owner-hero">
          <div>
            <span class="owner-eyebrow">
              ROBO CASK & TAP
            </span>

            <h2>Owner View</h2>

            <p>
              Live read-only business information from the
              main shop computer.
            </p>
          </div>

          <div class="owner-connection">
            ${ownerStatusBadge("POS connected", "active")}

            <small>
              Updated ${escapeHtml(
                ownerFormatDateTime(refreshedAt)
              )}
            </small>
          </div>
        </section>

        <section class="owner-period-bar">
          <div>
            <strong>Reporting period</strong>
            <small>${escapeHtml(ownerPeriodLabel(ownerDashboardUi.periodDays))}</small>
          </div>

          <div class="owner-period-actions">
            <button
              type="button"
              data-owner-period="1"
              class="${
                ownerDashboardUi.periodDays === 1
                  ? "primary"
                  : ""
              }"
            >
              Today
            </button>

            <button
              type="button"
              data-owner-period="7"
              class="${
                ownerDashboardUi.periodDays === 7
                  ? "primary"
                  : ""
              }"
            >
              7 days
            </button>

            <button
              type="button"
              data-owner-period="30"
              class="${
                ownerDashboardUi.periodDays === 30
                  ? "primary"
                  : ""
              }"
            >
              30 days
            </button>

            <button
              id="refreshOwnerDashboard"
              type="button"
            >
              Refresh
            </button>
          </div>
        </section>

        <section class="owner-metrics">
          ${ownerMetric(
            "Revenue",
            money(report.revenueMinor),
            `${report.salesCount} completed sales`
          )}

          ${ownerMetric(
            "Gross profit",
            money(report.grossProfitMinor),
            "Before business expenses"
          )}

          ${ownerMetric(
            "Net profit",
            money(report.netProfitMinor),
            `Expenses: ${money(report.expenseTotalMinor)}`
          )}

          ${ownerMetric(
            "Low stock",
            lowStock.length,
            `${products.length} active products`
          )}

          ${ownerMetric(
            "Open shifts",
            summary.openShifts,
            "Tellers currently working"
          )}

          ${ownerMetric(
            "All-time sales",
            summary.completedSales,
            money(summary.totalSalesMinor)
          )}
        </section>

        <section class="owner-dashboard-grid">
          <article class="panel owner-alert-panel">
            <div class="owner-section-heading">
              <div>
                <h3>Low-stock alerts</h3>
                <p>Products needing attention.</p>
              </div>

              ${ownerStatusBadge(
                `${lowStock.length} alert${lowStock.length === 1 ? "" : "s"}`,
                lowStock.length ? "warning" : "active"
              )}
            </div>

            <div class="owner-alert-list">
              ${
                lowStock.length
                  ? lowStock.slice(0, 12).map((product) => `
                      <div class="owner-alert-row">
                        <div>
                          <strong>${escapeHtml(product.name)}</strong>

                          <small>
                            ${escapeHtml(product.sku)}
                          </small>
                        </div>

                        <strong>
                          ${escapeHtml(
                            ownerFormatQuantity(
                              product.availableBaseUnits,
                              product.stockUnit
                            )
                          )}
                        </strong>
                      </div>
                    `).join("")
                  : `
                    <div class="owner-good-state">
                      No active product is below its
                      low-stock threshold.
                    </div>
                  `
              }
            </div>
          </article>

          <article class="panel owner-teller-panel">
            <div class="owner-section-heading">
              <div>
                <h3>Teller performance</h3>

                <p>
                  ${escapeHtml(
                    ownerPeriodLabel(ownerDashboardUi.periodDays)
                  )}
                </p>
              </div>
            </div>

            <div class="owner-teller-list">
              ${
                report.tellerPerformance?.length
                  ? report.tellerPerformance.map((teller) => `
                      <div class="owner-teller-row">
                        <div>
                          <strong>
                            ${escapeHtml(teller.tellerName)}
                          </strong>

                          <small>
                            ${Number(teller.salesCount || 0)}
                            completed sales
                          </small>
                        </div>

                        <strong>
                          ${escapeHtml(
                            money(teller.revenueMinor)
                          )}
                        </strong>
                      </div>
                    `).join("")
                  : `
                    <div class="empty">
                      No teller sales in this period.
                    </div>
                  `
              }
            </div>
          </article>
        </section>

        <section class="panel owner-stock-panel">
          <div class="owner-section-heading owner-stock-heading">
            <div>
              <h3>Stock availability</h3>

              <p>
                Current quantities available for sale.
              </p>
            </div>

            <input
              id="ownerStockSearch"
              type="search"
              placeholder="Search product or SKU"
              autocomplete="off"
            >
          </div>

          <div
            id="ownerStockRows"
            class="owner-stock-list"
          ></div>
        </section>

        <section class="owner-readonly-note">
          <strong>Read-only owner dashboard</strong>

          <span>
            Prices, stock and sales cannot be changed from
            this screen.
          </span>
        </section>
      </div>
    `;

    renderOwnerStockRows();

    $("#ownerStockSearch").addEventListener(
      "input",
      (event) => {
        renderOwnerStockRows(event.target.value);
      }
    );

    $("#refreshOwnerDashboard").addEventListener(
      "click",
      () => renderOwnerDashboard()
    );

    document
      .querySelectorAll("[data-owner-period]")
      .forEach((button) => {
        button.addEventListener("click", () => {
          ownerDashboardUi.periodDays = Number(
            button.dataset.ownerPeriod
          );

          renderOwnerDashboard();
        });
      });

    clearTimeout(ownerDashboardUi.refreshTimer);

    ownerDashboardUi.refreshTimer = setTimeout(() => {
      if (
        document.querySelector(
          "[data-owner-dashboard-root]"
        )
      ) {
        renderOwnerDashboard({
          silent: true
        });
      }
    }, 60000);
  } catch (error) {
    page.innerHTML = `
      <section
        class="panel owner-dashboard-error"
        data-owner-dashboard-root
      >
        <h2>Owner information is unavailable</h2>

        <p>
          Confirm that the main shop computer is running and
          this device is connected to the permitted network.
        </p>

        <button
          id="retryOwnerDashboard"
          type="button"
          class="primary"
        >
          Try again
        </button>
      </section>
    `;

    $("#retryOwnerDashboard")?.addEventListener(
      "click",
      () => renderOwnerDashboard()
    );

    handleError(error);
  } finally {
    ownerDashboardUi.isLoading = false;
  }
}
