CREATE TABLE IF NOT EXISTS hrm_departments
(
    id                  TEXT PRIMARY KEY,
    organization_id     TEXT NOT NULL,
    code                TEXT NOT NULL COLLATE NOCASE,
    name                TEXT NOT NULL,
    description         TEXT NOT NULL DEFAULT '',
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    version             INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id  TEXT NOT NULL,
    updated_by_user_id  TEXT NOT NULL,
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (updated_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (organization_id, code),
    UNIQUE (organization_id, name)
);

CREATE INDEX IF NOT EXISTS ix_hrm_departments_org_active
    ON hrm_departments(organization_id, is_active, name COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS hrm_positions
(
    id                  TEXT PRIMARY KEY,
    organization_id     TEXT NOT NULL,
    department_id       TEXT NOT NULL,
    code                TEXT NOT NULL COLLATE NOCASE,
    title               TEXT NOT NULL,
    description         TEXT NOT NULL DEFAULT '',
    grade               TEXT NOT NULL DEFAULT '',
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    version             INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id  TEXT NOT NULL,
    updated_by_user_id  TEXT NOT NULL,
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (department_id) REFERENCES hrm_departments(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (updated_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (organization_id, code)
);

CREATE INDEX IF NOT EXISTS ix_hrm_positions_department
    ON hrm_positions(organization_id, department_id, is_active, title COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS hrm_employees
(
    id                      TEXT PRIMARY KEY,
    organization_id         TEXT NOT NULL,
    home_shop_id            TEXT NOT NULL,
    user_id                 TEXT NULL,
    department_id           TEXT NOT NULL,
    position_id             TEXT NOT NULL,
    employee_number         TEXT NOT NULL COLLATE NOCASE,
    first_name              TEXT NOT NULL,
    last_name               TEXT NOT NULL,
    other_names             TEXT NOT NULL DEFAULT '',
    preferred_name          TEXT NOT NULL DEFAULT '',
    phone                   TEXT NOT NULL DEFAULT '',
    email                   TEXT NOT NULL DEFAULT '',
    address                 TEXT NOT NULL DEFAULT '',
    emergency_contact_name  TEXT NOT NULL DEFAULT '',
    emergency_contact_phone TEXT NOT NULL DEFAULT '',
    employment_type         TEXT NOT NULL DEFAULT 'permanent'
                            CHECK (employment_type IN ('permanent','contract','temporary','casual','intern','consultant')),
    hire_date               TEXT NOT NULL CHECK (length(hire_date) = 10 AND date(hire_date) = hire_date),
    end_date                TEXT NULL CHECK (end_date IS NULL OR (length(end_date) = 10 AND date(end_date) = end_date)),
    status                  TEXT NOT NULL DEFAULT 'active'
                            CHECK (status IN ('active','probation','suspended','on_leave','terminated','resigned','retired')),
    base_salary_minor       INTEGER NOT NULL DEFAULT 0 CHECK (base_salary_minor >= 0),
    pay_frequency           TEXT NOT NULL DEFAULT 'monthly'
                            CHECK (pay_frequency IN ('monthly','weekly','daily','hourly')),
    standard_hours_per_week REAL NOT NULL DEFAULT 45 CHECK (standard_hours_per_week > 0 AND standard_hours_per_week <= 168),
    tax_number              TEXT NOT NULL DEFAULT '',
    national_id             TEXT NOT NULL DEFAULT '',
    bank_name               TEXT NOT NULL DEFAULT '',
    bank_account            TEXT NOT NULL DEFAULT '',
    mobile_money_number     TEXT NOT NULL DEFAULT '',
    notes                   TEXT NOT NULL DEFAULT '',
    version                 INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id      TEXT NOT NULL,
    updated_by_user_id      TEXT NOT NULL,
    created_at_utc          TEXT NOT NULL,
    updated_at_utc          TEXT NOT NULL,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (home_shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (department_id) REFERENCES hrm_departments(id) ON DELETE RESTRICT,
    FOREIGN KEY (position_id) REFERENCES hrm_positions(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (updated_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (organization_id, employee_number)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_hrm_employees_user
    ON hrm_employees(user_id)
    WHERE user_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_hrm_employees_org_status
    ON hrm_employees(organization_id, status, last_name COLLATE NOCASE, first_name COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS ix_hrm_employees_home_shop
    ON hrm_employees(organization_id, home_shop_id, status);

CREATE TABLE IF NOT EXISTS hrm_employee_shop_assignments
(
    employee_id         TEXT NOT NULL,
    shop_id             TEXT NOT NULL,
    assignment_type     TEXT NOT NULL DEFAULT 'secondary'
                        CHECK (assignment_type IN ('home','secondary','temporary')),
    effective_from      TEXT NOT NULL CHECK (length(effective_from) = 10 AND date(effective_from) = effective_from),
    effective_to        TEXT NULL CHECK (effective_to IS NULL OR (length(effective_to) = 10 AND date(effective_to) = effective_to)),
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    assigned_by_user_id TEXT NOT NULL,
    created_at_utc      TEXT NOT NULL,
    PRIMARY KEY (employee_id, shop_id),
    FOREIGN KEY (employee_id) REFERENCES hrm_employees(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (assigned_by_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_hrm_assignments_shop
    ON hrm_employee_shop_assignments(shop_id, is_active, effective_from, effective_to);

CREATE TABLE IF NOT EXISTS hrm_work_schedules
(
    id                  TEXT PRIMARY KEY,
    organization_id     TEXT NOT NULL,
    shop_id             TEXT NOT NULL,
    employee_id         TEXT NOT NULL,
    work_date           TEXT NOT NULL CHECK (length(work_date) = 10 AND date(work_date) = work_date),
    start_time          TEXT NOT NULL CHECK (length(start_time) = 5),
    end_time            TEXT NOT NULL CHECK (length(end_time) = 5),
    break_minutes       INTEGER NOT NULL DEFAULT 0 CHECK (break_minutes >= 0 AND break_minutes <= 720),
    status              TEXT NOT NULL DEFAULT 'draft'
                        CHECK (status IN ('draft','published','cancelled')),
    notes               TEXT NOT NULL DEFAULT '',
    version             INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id  TEXT NOT NULL,
    updated_by_user_id  TEXT NOT NULL,
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (employee_id) REFERENCES hrm_employees(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (updated_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (employee_id, work_date)
);

CREATE INDEX IF NOT EXISTS ix_hrm_schedules_shop_date
    ON hrm_work_schedules(organization_id, shop_id, work_date, status);

CREATE TABLE IF NOT EXISTS hrm_attendance_entries
(
    id                  TEXT PRIMARY KEY,
    organization_id     TEXT NOT NULL,
    shop_id             TEXT NOT NULL,
    employee_id         TEXT NOT NULL,
    work_date           TEXT NOT NULL CHECK (length(work_date) = 10 AND date(work_date) = work_date),
    clock_in_utc        TEXT NOT NULL,
    clock_out_utc       TEXT NULL,
    break_minutes       INTEGER NOT NULL DEFAULT 0 CHECK (break_minutes >= 0 AND break_minutes <= 720),
    worked_minutes      INTEGER NULL CHECK (worked_minutes IS NULL OR worked_minutes >= 0),
    overtime_minutes    INTEGER NOT NULL DEFAULT 0 CHECK (overtime_minutes >= 0),
    status              TEXT NOT NULL DEFAULT 'open'
                        CHECK (status IN ('open','completed','approved','rejected','corrected')),
    source              TEXT NOT NULL DEFAULT 'manual'
                        CHECK (source IN ('manual','device','import','schedule')),
    notes               TEXT NOT NULL DEFAULT '',
    version             INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id  TEXT NOT NULL,
    approved_by_user_id TEXT NULL,
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    approved_at_utc     TEXT NULL,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (shop_id) REFERENCES shops(id) ON DELETE RESTRICT,
    FOREIGN KEY (employee_id) REFERENCES hrm_employees(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (approved_by_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_hrm_attendance_open_employee
    ON hrm_attendance_entries(employee_id)
    WHERE status = 'open';
CREATE INDEX IF NOT EXISTS ix_hrm_attendance_shop_date
    ON hrm_attendance_entries(organization_id, shop_id, work_date, status);

CREATE TABLE IF NOT EXISTS hrm_leave_types
(
    id                  TEXT PRIMARY KEY,
    organization_id     TEXT NOT NULL,
    code                TEXT NOT NULL COLLATE NOCASE,
    name                TEXT NOT NULL,
    annual_entitlement_days REAL NOT NULL DEFAULT 0 CHECK (annual_entitlement_days >= 0 AND annual_entitlement_days <= 366),
    is_paid             INTEGER NOT NULL DEFAULT 1 CHECK (is_paid IN (0,1)),
    requires_attachment INTEGER NOT NULL DEFAULT 0 CHECK (requires_attachment IN (0,1)),
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    version             INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id  TEXT NOT NULL,
    updated_by_user_id  TEXT NOT NULL,
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (updated_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (organization_id, code),
    UNIQUE (organization_id, name)
);

CREATE TABLE IF NOT EXISTS hrm_leave_requests
(
    id                  TEXT PRIMARY KEY,
    organization_id     TEXT NOT NULL,
    employee_id         TEXT NOT NULL,
    leave_type_id       TEXT NOT NULL,
    start_date          TEXT NOT NULL CHECK (length(start_date) = 10 AND date(start_date) = start_date),
    end_date            TEXT NOT NULL CHECK (length(end_date) = 10 AND date(end_date) = end_date),
    requested_days      REAL NOT NULL CHECK (requested_days > 0 AND requested_days <= 366),
    reason              TEXT NOT NULL DEFAULT '',
    attachment_reference TEXT NOT NULL DEFAULT '',
    status              TEXT NOT NULL DEFAULT 'draft'
                        CHECK (status IN ('draft','submitted','approved','rejected','cancelled')),
    version             INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    requested_by_user_id TEXT NOT NULL,
    decided_by_user_id  TEXT NULL,
    decision_notes      TEXT NOT NULL DEFAULT '',
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    submitted_at_utc    TEXT NULL,
    decided_at_utc      TEXT NULL,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (employee_id) REFERENCES hrm_employees(id) ON DELETE RESTRICT,
    FOREIGN KEY (leave_type_id) REFERENCES hrm_leave_types(id) ON DELETE RESTRICT,
    FOREIGN KEY (requested_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (decided_by_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_hrm_leave_employee_dates
    ON hrm_leave_requests(organization_id, employee_id, start_date, end_date, status);

CREATE TABLE IF NOT EXISTS hrm_payroll_periods
(
    id                  TEXT PRIMARY KEY,
    organization_id     TEXT NOT NULL,
    name                TEXT NOT NULL,
    start_date          TEXT NOT NULL CHECK (length(start_date) = 10 AND date(start_date) = start_date),
    end_date            TEXT NOT NULL CHECK (length(end_date) = 10 AND date(end_date) = end_date),
    pay_date            TEXT NOT NULL CHECK (length(pay_date) = 10 AND date(pay_date) = pay_date),
    status              TEXT NOT NULL DEFAULT 'draft'
                        CHECK (status IN ('draft','calculated','approved','closed','cancelled')),
    version             INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id  TEXT NOT NULL,
    approved_by_user_id TEXT NULL,
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    approved_at_utc     TEXT NULL,
    closed_at_utc       TEXT NULL,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (approved_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (organization_id, start_date, end_date)
);

CREATE TABLE IF NOT EXISTS hrm_payroll_entries
(
    id                  TEXT PRIMARY KEY,
    payroll_period_id   TEXT NOT NULL,
    employee_id         TEXT NOT NULL,
    base_pay_minor      INTEGER NOT NULL CHECK (base_pay_minor >= 0),
    overtime_pay_minor  INTEGER NOT NULL DEFAULT 0 CHECK (overtime_pay_minor >= 0),
    allowance_minor     INTEGER NOT NULL DEFAULT 0 CHECK (allowance_minor >= 0),
    deduction_minor     INTEGER NOT NULL DEFAULT 0 CHECK (deduction_minor >= 0),
    gross_pay_minor     INTEGER NOT NULL CHECK (gross_pay_minor >= 0),
    net_pay_minor       INTEGER NOT NULL CHECK (net_pay_minor >= 0),
    worked_minutes      INTEGER NOT NULL DEFAULT 0 CHECK (worked_minutes >= 0),
    overtime_minutes    INTEGER NOT NULL DEFAULT 0 CHECK (overtime_minutes >= 0),
    notes               TEXT NOT NULL DEFAULT '',
    created_at_utc      TEXT NOT NULL,
    FOREIGN KEY (payroll_period_id) REFERENCES hrm_payroll_periods(id) ON DELETE RESTRICT,
    FOREIGN KEY (employee_id) REFERENCES hrm_employees(id) ON DELETE RESTRICT,
    UNIQUE (payroll_period_id, employee_id)
);

CREATE INDEX IF NOT EXISTS ix_hrm_payroll_entries_employee
    ON hrm_payroll_entries(employee_id, payroll_period_id);

CREATE TABLE IF NOT EXISTS hrm_performance_reviews
(
    id                  TEXT PRIMARY KEY,
    organization_id     TEXT NOT NULL,
    employee_id         TEXT NOT NULL,
    reviewer_user_id    TEXT NOT NULL,
    review_period_start TEXT NOT NULL CHECK (length(review_period_start) = 10 AND date(review_period_start) = review_period_start),
    review_period_end   TEXT NOT NULL CHECK (length(review_period_end) = 10 AND date(review_period_end) = review_period_end),
    goals               TEXT NOT NULL DEFAULT '',
    achievements        TEXT NOT NULL DEFAULT '',
    improvement_areas   TEXT NOT NULL DEFAULT '',
    overall_rating      INTEGER NULL CHECK (overall_rating IS NULL OR overall_rating BETWEEN 1 AND 5),
    status              TEXT NOT NULL DEFAULT 'draft'
                        CHECK (status IN ('draft','completed','acknowledged','cancelled')),
    version             INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    completed_at_utc    TEXT NULL,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (employee_id) REFERENCES hrm_employees(id) ON DELETE RESTRICT,
    FOREIGN KEY (reviewer_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS hrm_training_records
(
    id                  TEXT PRIMARY KEY,
    organization_id     TEXT NOT NULL,
    employee_id         TEXT NOT NULL,
    title               TEXT NOT NULL,
    provider            TEXT NOT NULL DEFAULT '',
    start_date          TEXT NOT NULL CHECK (length(start_date) = 10 AND date(start_date) = start_date),
    end_date            TEXT NULL CHECK (end_date IS NULL OR (length(end_date) = 10 AND date(end_date) = end_date)),
    expiry_date         TEXT NULL CHECK (expiry_date IS NULL OR (length(expiry_date) = 10 AND date(expiry_date) = expiry_date)),
    cost_minor          INTEGER NOT NULL DEFAULT 0 CHECK (cost_minor >= 0),
    status              TEXT NOT NULL DEFAULT 'planned'
                        CHECK (status IN ('planned','in_progress','completed','failed','cancelled')),
    certificate_reference TEXT NOT NULL DEFAULT '',
    notes               TEXT NOT NULL DEFAULT '',
    version             INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    created_by_user_id  TEXT NOT NULL,
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    completed_at_utc    TEXT NULL,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (employee_id) REFERENCES hrm_employees(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS hrm_disciplinary_cases
(
    id                  TEXT PRIMARY KEY,
    organization_id     TEXT NOT NULL,
    employee_id         TEXT NOT NULL,
    case_number         TEXT NOT NULL COLLATE NOCASE,
    incident_date       TEXT NOT NULL CHECK (length(incident_date) = 10 AND date(incident_date) = incident_date),
    category            TEXT NOT NULL,
    severity            TEXT NOT NULL DEFAULT 'minor'
                        CHECK (severity IN ('minor','moderate','major','critical')),
    description         TEXT NOT NULL,
    action_taken        TEXT NOT NULL DEFAULT '',
    status              TEXT NOT NULL DEFAULT 'open'
                        CHECK (status IN ('open','under_review','resolved','dismissed','appealed')),
    version             INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
    opened_by_user_id   TEXT NOT NULL,
    resolved_by_user_id TEXT NULL,
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    resolved_at_utc     TEXT NULL,
    FOREIGN KEY (organization_id) REFERENCES organizations(id) ON DELETE RESTRICT,
    FOREIGN KEY (employee_id) REFERENCES hrm_employees(id) ON DELETE RESTRICT,
    FOREIGN KEY (opened_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    FOREIGN KEY (resolved_by_user_id) REFERENCES users(id) ON DELETE RESTRICT,
    UNIQUE (organization_id, case_number)
);

CREATE TRIGGER IF NOT EXISTS trg_hrm_department_ownership_update
BEFORE UPDATE ON hrm_departments
WHEN NEW.organization_id <> OLD.organization_id OR NEW.code <> OLD.code
BEGIN SELECT RAISE(ABORT, 'department ownership and code are immutable'); END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_position_scope_insert
BEFORE INSERT ON hrm_positions
BEGIN
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1 FROM hrm_departments
        WHERE id = NEW.department_id AND organization_id = NEW.organization_id
    ) THEN RAISE(ABORT, 'position department scope is invalid') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_employee_scope_insert
BEFORE INSERT ON hrm_employees
BEGIN
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1
        FROM shops AS shop
        INNER JOIN hrm_departments AS department ON department.id = NEW.department_id
        INNER JOIN hrm_positions AS position ON position.id = NEW.position_id
        WHERE shop.id = NEW.home_shop_id
          AND shop.organization_id = NEW.organization_id
          AND department.organization_id = NEW.organization_id
          AND position.organization_id = NEW.organization_id
          AND position.department_id = NEW.department_id
    ) THEN RAISE(ABORT, 'employee organization, shop, department or position scope is invalid') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_employee_ownership_update
BEFORE UPDATE ON hrm_employees
WHEN NEW.organization_id <> OLD.organization_id OR NEW.employee_number <> OLD.employee_number OR NEW.created_at_utc <> OLD.created_at_utc
BEGIN SELECT RAISE(ABORT, 'employee ownership and number are immutable'); END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_assignment_scope_insert
BEFORE INSERT ON hrm_employee_shop_assignments
BEGIN
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1 FROM hrm_employees AS employee
        INNER JOIN shops AS shop ON shop.id = NEW.shop_id
        WHERE employee.id = NEW.employee_id
          AND employee.organization_id = shop.organization_id
    ) THEN RAISE(ABORT, 'employee shop assignment scope is invalid') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_schedule_scope_insert
BEFORE INSERT ON hrm_work_schedules
BEGIN
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1 FROM hrm_employees AS employee
        INNER JOIN shops AS shop ON shop.id = NEW.shop_id
        WHERE employee.id = NEW.employee_id
          AND employee.organization_id = NEW.organization_id
          AND shop.organization_id = NEW.organization_id
    ) THEN RAISE(ABORT, 'work schedule scope is invalid') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_schedule_state_update
BEFORE UPDATE OF status ON hrm_work_schedules
BEGIN
    SELECT CASE WHEN NOT
    (
        OLD.status = NEW.status
        OR (OLD.status = 'draft' AND NEW.status IN ('published','cancelled'))
        OR (OLD.status = 'published' AND NEW.status = 'cancelled')
    ) THEN RAISE(ABORT, 'invalid work schedule state transition') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_attendance_scope_insert
BEFORE INSERT ON hrm_attendance_entries
BEGIN
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1 FROM hrm_employees AS employee
        INNER JOIN shops AS shop ON shop.id = NEW.shop_id
        WHERE employee.id = NEW.employee_id
          AND employee.organization_id = NEW.organization_id
          AND shop.organization_id = NEW.organization_id
    ) THEN RAISE(ABORT, 'attendance scope is invalid') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_attendance_completion_update
BEFORE UPDATE ON hrm_attendance_entries
WHEN NEW.status IN ('completed','approved','rejected','corrected')
BEGIN
    SELECT CASE WHEN NEW.clock_out_utc IS NULL OR NEW.worked_minutes IS NULL
        THEN RAISE(ABORT, 'completed attendance requires clock out and worked minutes') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_leave_scope_insert
BEFORE INSERT ON hrm_leave_requests
BEGIN
    SELECT CASE WHEN NEW.end_date < NEW.start_date
        THEN RAISE(ABORT, 'leave end date precedes start date') END;
    SELECT CASE WHEN NOT EXISTS
    (
        SELECT 1 FROM hrm_employees AS employee
        INNER JOIN hrm_leave_types AS leave_type ON leave_type.id = NEW.leave_type_id
        WHERE employee.id = NEW.employee_id
          AND employee.organization_id = NEW.organization_id
          AND leave_type.organization_id = NEW.organization_id
          AND leave_type.is_active = 1
    ) THEN RAISE(ABORT, 'leave request scope is invalid') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_leave_state_update
BEFORE UPDATE OF status ON hrm_leave_requests
BEGIN
    SELECT CASE WHEN NOT
    (
        OLD.status = NEW.status
        OR (OLD.status = 'draft' AND NEW.status IN ('submitted','cancelled'))
        OR (OLD.status = 'submitted' AND NEW.status IN ('approved','rejected','cancelled'))
        OR (OLD.status = 'approved' AND NEW.status = 'cancelled')
    ) THEN RAISE(ABORT, 'invalid leave request state transition') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_leave_overlap_approval
BEFORE UPDATE OF status ON hrm_leave_requests
WHEN NEW.status = 'approved' AND OLD.status <> 'approved'
BEGIN
    SELECT CASE WHEN EXISTS
    (
        SELECT 1 FROM hrm_leave_requests AS other
        WHERE other.employee_id = NEW.employee_id
          AND other.id <> NEW.id
          AND other.status = 'approved'
          AND other.start_date <= NEW.end_date
          AND other.end_date >= NEW.start_date
    ) THEN RAISE(ABORT, 'approved leave overlaps another approved request') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_payroll_period_dates_insert
BEFORE INSERT ON hrm_payroll_periods
BEGIN
    SELECT CASE WHEN NEW.end_date < NEW.start_date OR NEW.pay_date < NEW.end_date
        THEN RAISE(ABORT, 'payroll period dates are invalid') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_payroll_state_update
BEFORE UPDATE OF status ON hrm_payroll_periods
BEGIN
    SELECT CASE WHEN NOT
    (
        OLD.status = NEW.status
        OR (OLD.status = 'draft' AND NEW.status IN ('calculated','cancelled'))
        OR (OLD.status = 'calculated' AND NEW.status IN ('approved','draft','cancelled'))
        OR (OLD.status = 'approved' AND NEW.status = 'closed')
    ) THEN RAISE(ABORT, 'invalid payroll period state transition') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_performance_state_update
BEFORE UPDATE OF status ON hrm_performance_reviews
BEGIN
    SELECT CASE WHEN NOT
    (
        OLD.status = NEW.status
        OR (OLD.status = 'draft' AND NEW.status IN ('completed','cancelled'))
        OR (OLD.status = 'completed' AND NEW.status = 'acknowledged')
    ) THEN RAISE(ABORT, 'invalid performance review state transition') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_training_state_update
BEFORE UPDATE OF status ON hrm_training_records
BEGIN
    SELECT CASE WHEN NOT
    (
        OLD.status = NEW.status
        OR (OLD.status = 'planned' AND NEW.status IN ('in_progress','completed','cancelled'))
        OR (OLD.status = 'in_progress' AND NEW.status IN ('completed','failed','cancelled'))
    ) THEN RAISE(ABORT, 'invalid training state transition') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_disciplinary_state_update
BEFORE UPDATE OF status ON hrm_disciplinary_cases
BEGIN
    SELECT CASE WHEN NOT
    (
        OLD.status = NEW.status
        OR (OLD.status = 'open' AND NEW.status IN ('under_review','resolved','dismissed'))
        OR (OLD.status = 'under_review' AND NEW.status IN ('resolved','dismissed'))
        OR (OLD.status = 'resolved' AND NEW.status = 'appealed')
        OR (OLD.status = 'appealed' AND NEW.status IN ('resolved','dismissed'))
    ) THEN RAISE(ABORT, 'invalid disciplinary case state transition') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_hrm_employee_delete
BEFORE DELETE ON hrm_employees
BEGIN SELECT RAISE(ABORT, 'employees are permanent audit records'); END;
CREATE TRIGGER IF NOT EXISTS trg_hrm_attendance_delete
BEFORE DELETE ON hrm_attendance_entries
BEGIN SELECT RAISE(ABORT, 'attendance records are permanent audit records'); END;
CREATE TRIGGER IF NOT EXISTS trg_hrm_leave_delete
BEFORE DELETE ON hrm_leave_requests
BEGIN SELECT RAISE(ABORT, 'leave requests are permanent audit records'); END;
CREATE TRIGGER IF NOT EXISTS trg_hrm_payroll_delete
BEFORE DELETE ON hrm_payroll_periods
BEGIN SELECT RAISE(ABORT, 'payroll periods are permanent audit records'); END;
CREATE TRIGGER IF NOT EXISTS trg_hrm_performance_delete
BEFORE DELETE ON hrm_performance_reviews
BEGIN SELECT RAISE(ABORT, 'performance reviews are permanent audit records'); END;
CREATE TRIGGER IF NOT EXISTS trg_hrm_training_delete
BEFORE DELETE ON hrm_training_records
BEGIN SELECT RAISE(ABORT, 'training records are permanent audit records'); END;
CREATE TRIGGER IF NOT EXISTS trg_hrm_disciplinary_delete
BEFORE DELETE ON hrm_disciplinary_cases
BEGIN SELECT RAISE(ABORT, 'disciplinary cases are permanent audit records'); END;

CREATE VIEW IF NOT EXISTS hrm_employee_attendance_summary AS
SELECT
    employee.organization_id,
    employee.id AS employee_id,
    COUNT(CASE WHEN attendance.status IN ('completed','approved','corrected') THEN 1 END) AS attendance_day_count,
    COALESCE(SUM(CASE WHEN attendance.status IN ('completed','approved','corrected') THEN attendance.worked_minutes ELSE 0 END), 0) AS worked_minutes,
    COALESCE(SUM(CASE WHEN attendance.status IN ('completed','approved','corrected') THEN attendance.overtime_minutes ELSE 0 END), 0) AS overtime_minutes
FROM hrm_employees AS employee
LEFT JOIN hrm_attendance_entries AS attendance ON attendance.employee_id = employee.id
GROUP BY employee.organization_id, employee.id;

CREATE VIEW IF NOT EXISTS hrm_employee_leave_summary AS
SELECT
    employee.organization_id,
    employee.id AS employee_id,
    COALESCE(SUM(CASE WHEN request.status = 'approved' THEN request.requested_days ELSE 0 END), 0) AS approved_leave_days,
    COUNT(CASE WHEN request.status = 'submitted' THEN 1 END) AS pending_leave_requests
FROM hrm_employees AS employee
LEFT JOIN hrm_leave_requests AS request ON request.employee_id = employee.id
GROUP BY employee.organization_id, employee.id;

INSERT OR IGNORE INTO schema_versions(version, description, applied_at_utc)
VALUES
(
    15,
    'HRM employees, departments, schedules, attendance, leave, payroll, performance, training and discipline',
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
);
