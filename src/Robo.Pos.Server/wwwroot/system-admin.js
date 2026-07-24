"use strict";

const systemAdminUi = {
  settings: null,
  backups: []
};

function systemAdminDateTime(value) {
  if (!value) {
    return "—";
  }

  return new Date(value).toLocaleString();
}

function systemAdminFileSize(sizeBytes) {
  const size = Number(sizeBytes || 0);

  if (size < 1024) {
    return `${size} bytes`;
  }

  if (size < 1024 * 1024) {
    return `${(size / 1024).toFixed(1)} KB`;
  }

  return `${(size / (1024 * 1024)).toFixed(2)} MB`;
}

function systemAdminShortHash(value) {
  const hash = String(value || "");

  if (hash.length <= 20) {
    return hash;
  }

  return `${hash.slice(0, 12)}…${hash.slice(-8)}`;
}

async function renderSystemAdministration() {
  const [settings, backupResult] = await Promise.all([
    api("/api/v3/admin/settings"),
    api("/api/v3/admin/backups")
  ]);

  systemAdminUi.settings = settings;
  systemAdminUi.backups = backupResult.backups;

  $("#page").innerHTML = `
    <section class="panel">
      <div class="system-admin-heading">
        <div>
          <h2>Business settings</h2>
          <p>
            These details appear on future receipts and invoices.
          </p>
        </div>

        <span class="business-status active">
          Baron only
        </span>
      </div>

      <form id="businessSettingsForm" class="form-grid">
        <label>
          Business name
          <input
            id="settingsBusinessName"
            maxlength="150"
            value="${escapeHtml(settings.businessName)}"
            required
          >
        </label>

        <label>
          Phone
          <input
            id="settingsPhone"
            maxlength="100"
            value="${escapeHtml(settings.phone)}"
          >
        </label>

        <label>
          Email
          <input
            id="settingsEmail"
            type="email"
            maxlength="200"
            value="${escapeHtml(settings.email)}"
          >
        </label>

        <label>
          Currency
          <input
            value="${escapeHtml(settings.currencyCode)}"
            readonly
          >
        </label>

        <label class="wide">
          Business address
          <textarea
            id="settingsAddress"
            maxlength="500"
            required
          >${escapeHtml(settings.address)}</textarea>
        </label>

        <label class="wide">
          Receipt footer
          <textarea
            id="settingsReceiptFooter"
            maxlength="500"
            required
          >${escapeHtml(settings.receiptFooter)}</textarea>
        </label>

        <div class="wide system-admin-note">
          Receipt verification codes are permanently
          <strong>disabled</strong>.
        </div>

        <button class="primary wide" type="submit">
          Save business settings
        </button>
      </form>
    </section>

    <section class="panel business-section">
      <h2>Protected storage locations</h2>

      <div class="system-path-grid">
        <article>
          <strong>SQLite database</strong>
          <code>${escapeHtml(settings.databasePath)}</code>
        </article>

        <article>
          <strong>Receipt and invoice documents</strong>
          <code>${escapeHtml(settings.documentRoot)}</code>
        </article>

        <article>
          <strong>Verified database backups</strong>
          <code>${escapeHtml(settings.backupRoot)}</code>
        </article>
      </div>
    </section>

    <section class="panel business-section">
      <div class="system-admin-heading">
        <div>
          <h2>Database backups</h2>
          <p>
            Create a verified snapshot of the live SQLite database.
          </p>
        </div>

        <button
          id="createSystemBackup"
          class="primary"
          type="button"
        >
          Create backup
        </button>
      </div>

      <div class="system-admin-note">
        Restoring a database must be performed while the POS server
        is stopped. Online restoration is intentionally disabled.
      </div>

      <div class="table-wrap system-backup-table">
        <table>
          <thead>
            <tr>
              <th>Backup file</th>
              <th>Created</th>
              <th>Size</th>
              <th>Integrity</th>
              <th>SHA-256</th>
              <th>Actions</th>
            </tr>
          </thead>

          <tbody>
            ${
              systemAdminUi.backups.length
                ? systemAdminUi.backups.map((backup) => `
                    <tr>
                      <td>
                        <strong>
                          ${escapeHtml(backup.fileName)}
                        </strong>
                      </td>

                      <td>
                        ${escapeHtml(
                          systemAdminDateTime(
                            backup.createdAtUtc
                          )
                        )}
                      </td>

                      <td>
                        ${escapeHtml(
                          systemAdminFileSize(
                            backup.sizeBytes
                          )
                        )}
                      </td>

                      <td>
                        <span class="business-status ${
                          backup.integrityOk
                            ? "active"
                            : "inactive"
                        }">
                          ${
                            backup.integrityOk
                              ? "Verified"
                              : "Failed"
                          }
                        </span>

                        <br>

                        <small>
                          Schema ${Number(
                            backup.schemaVersion || 0
                          )}
                        </small>
                      </td>

                      <td>
                        <code
                          title="${escapeHtml(backup.sha256)}"
                        >
                          ${escapeHtml(
                            systemAdminShortHash(
                              backup.sha256
                            )
                          )}
                        </code>
                      </td>

                      <td>
                        <div class="system-backup-actions">
                          <button
                            data-verify-system-backup="${escapeHtml(
                              backup.fileName
                            )}"
                            type="button"
                          >
                            Verify
                          </button>

                          <button
                            data-download-system-backup="${escapeHtml(
                              backup.fileName
                            )}"
                            type="button"
                          >
                            Download
                          </button>
                        </div>
                      </td>
                    </tr>
                  `).join("")
                : `
                  <tr>
                    <td colspan="6">
                      No database backups have been created.
                    </td>
                  </tr>
                `
            }
          </tbody>
        </table>
      </div>
    </section>
  `;

  $("#businessSettingsForm").addEventListener(
    "submit",
    saveSystemBusinessSettings
  );

  $("#createSystemBackup").addEventListener(
    "click",
    createSystemBackup
  );
}

async function saveSystemBusinessSettings(event) {
  event.preventDefault();

  try {
    const result = await api(
      "/api/v3/admin/settings",
      {
        method: "PUT",
        body: JSON.stringify({
          businessName:
            $("#settingsBusinessName").value.trim(),

          address:
            $("#settingsAddress").value.trim(),

          phone:
            $("#settingsPhone").value.trim(),

          email:
            $("#settingsEmail").value.trim(),

          receiptFooter:
            $("#settingsReceiptFooter").value.trim()
        })
      }
    );

    systemAdminUi.settings = result;

    showMessage(
      "Business settings saved and added to the audit trail."
    );

    await renderSystemAdministration();
  } catch (error) {
    handleError(error);
  }
}

async function createSystemBackup() {
  const button = $("#createSystemBackup");

  button.disabled = true;
  button.textContent = "Creating verified backup…";

  try {
    const backup = await api(
      "/api/v3/admin/backups",
      {
        method: "POST"
      }
    );

    showMessage(
      `${backup.fileName} created and verified successfully.`
    );

    await renderSystemAdministration();
  } catch (error) {
    handleError(error);
  } finally {
    if (button?.isConnected) {
      button.disabled = false;
      button.textContent = "Create backup";
    }
  }
}

async function verifySystemBackup(fileName) {
  try {
    const result = await api(
      `/api/v3/admin/backups/${
        encodeURIComponent(fileName)
      }/verify`,
      {
        method: "POST"
      }
    );

    if (result.integrityOk) {
      showMessage(
        `${result.fileName} passed SQLite integrity verification.`
      );
    } else {
      showMessage(
        `${result.fileName} failed integrity verification.`,
        true
      );
    }

    await renderSystemAdministration();
  } catch (error) {
    handleError(error);
  }
}

function downloadSystemBackup(fileName) {
  globalThis.location.assign(
    `/api/v3/admin/backups/${
      encodeURIComponent(fileName)
    }/download`
  );
}

document.addEventListener(
  "click",
  (event) => {
    const verifyButton = event.target.closest(
      "[data-verify-system-backup]"
    );

    const downloadButton = event.target.closest(
      "[data-download-system-backup]"
    );

    if (verifyButton) {
      verifySystemBackup(
        verifyButton.dataset.verifySystemBackup
      );
    }

    if (downloadButton) {
      downloadSystemBackup(
        downloadButton.dataset.downloadSystemBackup
      );
    }
  }
);
