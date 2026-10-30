-- Increment 9.5: program resources and cost loading (ADR-0049, docs/increments/9.5-program-resources.md). Forward-only, runs as keel_migrator.
-- Sorts after 0028_model_versions.sql (the version is the whole file name); the two migrations are independent.
--   * resource pools, roles (hourly rates are effective-dated, append-only rows), resources, assignments of resources to program tasks with an allocation in hours per working day;
--   * the cost plan: a monthly budget per cost type (every change logged), recorded actual costs (immutable; a correction is a reversing entry), material lines (a quantity of an
--     end item whose cost is estimated live from its BOM and the sourcing price lists by the host, never stored);
--   * over-allocation, cost loading, budget against actual against estimate to complete are computed from these rows by the module, over the rows the caller may read.
-- Marked data: pools, roles, resources, assignments, budget lines, actual costs and material lines carry the marking columns of ADR-0004 and cover the program's markings.
-- Child rows (role rates, budget log, counters) carry none: they are readable only through readable parents. Nothing here stores a person, a user claim or a membership.

CREATE FUNCTION prg.reject_cost_modification() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'recorded costs, rates and the budget log are never modified or deleted (% on %)', TG_OP, TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;

-- Per-program counters for the keys of this module (RP-, RR-, RS-, AS-, BL-, AC-, ML-). The program row is locked by every change, so a counter never races.
CREATE TABLE prg.res_counter (
    program_id uuid NOT NULL REFERENCES prg.program (id),
    kind       text NOT NULL CHECK (kind IN ('pool', 'role', 'resource', 'assignment', 'budget', 'actual', 'material')),
    next_no    int  NOT NULL DEFAULT 1,
    PRIMARY KEY (program_id, kind)
);

-- ================================================================ pools, roles and rates
CREATE TABLE prg.res_pool (
    id                     uuid        PRIMARY KEY,
    program_id             uuid        NOT NULL REFERENCES prg.program (id),
    key                    text        NOT NULL CHECK (key ~ '^RP-[0-9]{3,}$'),
    name                   text        NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 120),
    description            text        NOT NULL DEFAULT '' CHECK (length(description) <= 1000),
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text        NOT NULL,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL,
    created_at             timestamptz NOT NULL,
    created_by             text        NOT NULL,
    UNIQUE (program_id, key)
);
CREATE UNIQUE INDEX res_pool_name ON prg.res_pool (program_id, lower(name));

CREATE TABLE prg.res_role (
    id                     uuid        PRIMARY KEY,
    program_id             uuid        NOT NULL REFERENCES prg.program (id),
    pool_id                uuid        NOT NULL REFERENCES prg.res_pool (id),
    key                    text        NOT NULL CHECK (key ~ '^RR-[0-9]{3,}$'),
    name                   text        NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 120),
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text        NOT NULL,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL,
    created_at             timestamptz NOT NULL,
    created_by             text        NOT NULL,
    UNIQUE (program_id, key)
);
CREATE UNIQUE INDEX res_role_name ON prg.res_role (program_id, lower(name));

-- One row per rate change: the rate in force on a day is the latest row on or before it. Never modified.
CREATE TABLE prg.role_rate (
    id             uuid          PRIMARY KEY,
    role_id        uuid          NOT NULL REFERENCES prg.res_role (id),
    effective_from date          NOT NULL,
    hourly_rate    numeric(12,2) NOT NULL CHECK (hourly_rate >= 0 AND hourly_rate <= 100000),
    created_at     timestamptz   NOT NULL,
    created_by     text          NOT NULL,
    UNIQUE (role_id, effective_from)
);
CREATE TRIGGER role_rate_immutable BEFORE UPDATE OR DELETE ON prg.role_rate FOR EACH ROW EXECUTE FUNCTION prg.reject_cost_modification();
CREATE TRIGGER role_rate_no_truncate BEFORE TRUNCATE ON prg.role_rate FOR EACH STATEMENT EXECUTE FUNCTION prg.reject_cost_modification();

-- ================================================================ resources and assignments
CREATE TABLE prg.resource (
    id                     uuid         PRIMARY KEY,
    program_id             uuid         NOT NULL REFERENCES prg.program (id),
    role_id                uuid         NOT NULL REFERENCES prg.res_role (id),
    key                    text         NOT NULL CHECK (key ~ '^RS-[0-9]{3,}$'),
    name                   text         NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 120),
    capacity_hours_per_day numeric(4,2) NOT NULL DEFAULT 8 CHECK (capacity_hours_per_day > 0 AND capacity_hours_per_day <= 24),
    available_from         date,
    available_to           date,
    active                 boolean      NOT NULL DEFAULT true,
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text        NOT NULL,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL,
    created_at             timestamptz  NOT NULL,
    created_by             text         NOT NULL,
    updated_at             timestamptz  NOT NULL,
    updated_by             text         NOT NULL,
    UNIQUE (program_id, key),
    CONSTRAINT resource_availability CHECK (available_from IS NULL OR available_to IS NULL OR available_from <= available_to)
);
CREATE UNIQUE INDEX resource_name ON prg.resource (program_id, lower(name));

CREATE TABLE prg.assignment (
    id                     uuid         PRIMARY KEY,
    program_id             uuid         NOT NULL REFERENCES prg.program (id),
    key                    text         NOT NULL CHECK (key ~ '^AS-[0-9]{3,}$'),
    item_id                uuid         NOT NULL REFERENCES prg.item (id),
    resource_id            uuid         NOT NULL REFERENCES prg.resource (id),
    start_date             date         NOT NULL,
    end_date               date         NOT NULL,
    hours_per_day          numeric(5,2) NOT NULL CHECK (hours_per_day > 0 AND hours_per_day <= 24),
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text        NOT NULL,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL,
    removed_at             timestamptz,
    removed_by             text,
    created_at             timestamptz  NOT NULL,
    created_by             text         NOT NULL,
    updated_at             timestamptz  NOT NULL,
    updated_by             text         NOT NULL,
    UNIQUE (program_id, key),
    CONSTRAINT assignment_dates CHECK (start_date <= end_date AND end_date - start_date <= 3660)
);
CREATE INDEX assignment_resource_idx ON prg.assignment (resource_id) WHERE removed_at IS NULL;
CREATE INDEX assignment_item_idx ON prg.assignment (item_id) WHERE removed_at IS NULL;

CREATE FUNCTION prg.guard_assignment() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE item_program uuid; item_kind text; resource_program uuid;
BEGIN
    SELECT program_id, kind INTO item_program, item_kind FROM prg.item WHERE id = NEW.item_id;
    SELECT program_id INTO resource_program FROM prg.resource WHERE id = NEW.resource_id;
    IF item_program IS DISTINCT FROM NEW.program_id OR resource_program IS DISTINCT FROM NEW.program_id THEN
        RAISE EXCEPTION 'an assignment joins a task and a resource of its own program' USING ERRCODE = 'check_violation';
    END IF;
    IF item_kind <> 'Task' THEN
        RAISE EXCEPTION 'resources are assigned to tasks, not to phases or milestones' USING ERRCODE = 'check_violation';
    END IF;
    IF TG_OP = 'UPDATE' AND (NEW.id <> OLD.id OR NEW.program_id <> OLD.program_id OR NEW.key <> OLD.key OR NEW.item_id <> OLD.item_id OR NEW.resource_id <> OLD.resource_id OR NEW.created_at <> OLD.created_at) THEN
        RAISE EXCEPTION 'assignment identity is immutable' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER assignment_guard BEFORE INSERT OR UPDATE ON prg.assignment FOR EACH ROW EXECUTE FUNCTION prg.guard_assignment();

-- ================================================================ the cost plan: budget, actuals and material lines
CREATE TABLE prg.budget_line (
    id                     uuid          PRIMARY KEY,
    program_id             uuid          NOT NULL REFERENCES prg.program (id),
    key                    text          NOT NULL CHECK (key ~ '^BL-[0-9]{3,}$'),
    cost_type              text          NOT NULL CHECK (cost_type IN ('Labor', 'Material', 'Other')),
    period                 date          NOT NULL CHECK (EXTRACT(day FROM period) = 1),
    amount                 numeric(14,2) NOT NULL CHECK (amount >= 0 AND amount <= 100000000000),
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text        NOT NULL,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL,
    created_at             timestamptz   NOT NULL,
    created_by             text          NOT NULL,
    updated_at             timestamptz   NOT NULL,
    updated_by             text          NOT NULL,
    UNIQUE (program_id, key),
    UNIQUE (program_id, cost_type, period)
);

-- Every change of a budget amount (child of the budget line, never modified).
CREATE TABLE prg.budget_log (
    id         bigserial     PRIMARY KEY,
    line_id    uuid          NOT NULL REFERENCES prg.budget_line (id),
    old_amount numeric(14,2),
    new_amount numeric(14,2) NOT NULL,
    at         timestamptz   NOT NULL,
    by         text          NOT NULL
);
CREATE TRIGGER budget_log_immutable BEFORE UPDATE OR DELETE ON prg.budget_log FOR EACH ROW EXECUTE FUNCTION prg.reject_cost_modification();
CREATE TRIGGER budget_log_no_truncate BEFORE TRUNCATE ON prg.budget_log FOR EACH STATEMENT EXECUTE FUNCTION prg.reject_cost_modification();

CREATE TABLE prg.actual_cost (
    id                     uuid          PRIMARY KEY,
    program_id             uuid          NOT NULL REFERENCES prg.program (id),
    key                    text          NOT NULL CHECK (key ~ '^AC-[0-9]{4,}$'),
    cost_type              text          NOT NULL CHECK (cost_type IN ('Labor', 'Material', 'Other')),
    incurred_on            date          NOT NULL,
    amount                 numeric(14,2) NOT NULL CHECK (amount <> 0 AND abs(amount) <= 100000000000),
    hours                  numeric(8,2)  CHECK (hours IS NULL OR hours > 0),
    resource_id            uuid          REFERENCES prg.resource (id),
    item_id                uuid          REFERENCES prg.item (id),
    reverses_id            uuid          REFERENCES prg.actual_cost (id),
    note                   text          NOT NULL DEFAULT '' CHECK (length(note) <= 200),
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text        NOT NULL,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL,
    created_at             timestamptz   NOT NULL,
    created_by             text          NOT NULL,
    UNIQUE (program_id, key),
    CONSTRAINT actual_labor_has_hours CHECK (cost_type <> 'Labor' OR reverses_id IS NOT NULL OR (hours IS NOT NULL AND resource_id IS NOT NULL)),
    CONSTRAINT actual_hours_only_labor CHECK (hours IS NULL OR cost_type = 'Labor')
);
CREATE INDEX actual_program_idx ON prg.actual_cost (program_id, incurred_on);
CREATE UNIQUE INDEX actual_reversed_once ON prg.actual_cost (reverses_id) WHERE reverses_id IS NOT NULL;
CREATE FUNCTION prg.guard_actual() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE o record;
BEGIN
    IF NEW.reverses_id IS NOT NULL THEN
        SELECT program_id, cost_type, amount, reverses_id INTO o FROM prg.actual_cost WHERE id = NEW.reverses_id;
        IF o.program_id IS DISTINCT FROM NEW.program_id OR o.cost_type <> NEW.cost_type OR o.amount <> -NEW.amount OR o.reverses_id IS NOT NULL THEN
            RAISE EXCEPTION 'a reversal cancels one earlier entry of the same program and cost type with the opposite amount' USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER actual_guard BEFORE INSERT ON prg.actual_cost FOR EACH ROW EXECUTE FUNCTION prg.guard_actual();
CREATE TRIGGER actual_immutable BEFORE UPDATE OR DELETE ON prg.actual_cost FOR EACH ROW EXECUTE FUNCTION prg.reject_cost_modification();
CREATE TRIGGER actual_no_truncate BEFORE TRUNCATE ON prg.actual_cost FOR EACH STATEMENT EXECUTE FUNCTION prg.reject_cost_modification();

-- A quantity of an end item (a part number of the product data) needed on a day. Its cost is read live from the BOM and the price lists; no price is stored here.
CREATE TABLE prg.material_line (
    id                     uuid          PRIMARY KEY,
    program_id             uuid          NOT NULL REFERENCES prg.program (id),
    key                    text          NOT NULL CHECK (key ~ '^ML-[0-9]{3,}$'),
    part_number            text          NOT NULL CHECK (length(part_number) BETWEEN 1 AND 64),
    quantity               numeric(14,4) NOT NULL CHECK (quantity > 0 AND quantity <= 1000000),
    needed_on              date          NOT NULL,
    note                   text          NOT NULL DEFAULT '' CHECK (length(note) <= 200),
    export_jurisdiction    text        NOT NULL CHECK (export_jurisdiction IN ('None', 'Ear', 'Itar')),
    eccn                   text,
    usml_category          text,
    cui_categories         text[]      NOT NULL,
    distribution_statement text        NOT NULL CHECK (distribution_statement IN ('A', 'B', 'C', 'D', 'E', 'F')),
    controlling_office     text,
    distribution_reason    text,
    program                text        NOT NULL,
    owning_org             text        NOT NULL,
    export_authorizations  text[]      NOT NULL,
    removed_at             timestamptz,
    removed_by             text,
    created_at             timestamptz   NOT NULL,
    created_by             text          NOT NULL,
    UNIQUE (program_id, key)
);
CREATE INDEX material_program_idx ON prg.material_line (program_id) WHERE removed_at IS NULL;

-- ================================================================ privileges (least privilege for keel_app)
GRANT SELECT, INSERT, UPDATE ON prg.res_pool, prg.res_role, prg.resource, prg.assignment, prg.budget_line, prg.material_line, prg.res_counter TO keel_app;
GRANT SELECT, INSERT ON prg.role_rate, prg.budget_log, prg.actual_cost TO keel_app;
GRANT USAGE ON SEQUENCE prg.budget_log_id_seq TO keel_app;
