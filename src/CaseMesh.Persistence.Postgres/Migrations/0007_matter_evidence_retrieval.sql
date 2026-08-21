CREATE INDEX source_spans_matter_fts_ix
    ON casemesh.source_spans USING gin (to_tsvector('simple', extracted_text));

CREATE INDEX assertions_matter_fts_ix
    ON casemesh.assertions USING gin (to_tsvector('simple',
        subject_reference || ' ' || predicate || ' ' || value || ' ' || asserted_by));

CREATE INDEX matter_events_matter_fts_ix
    ON casemesh.matter_events USING gin (to_tsvector('simple', event_type || ' ' || label));

CREATE INDEX people_matter_fts_ix
    ON casemesh.people USING gin (to_tsvector('simple', display_name));

CREATE INDEX organisations_matter_fts_ix
    ON casemesh.organisations USING gin (to_tsvector('simple', name || ' ' || type_label));

CREATE INDEX entity_aliases_matter_fts_ix
    ON casemesh.entity_aliases USING gin (to_tsvector('simple', alias_value || ' ' || normalized_value));

CREATE INDEX communications_matter_fts_ix
    ON casemesh.communications USING gin (to_tsvector('simple', neutral_label));

CREATE INDEX employment_terms_matter_fts_ix
    ON casemesh.employment_terms USING gin (to_tsvector('simple', term_value));

CREATE INDEX health_absence_records_matter_fts_ix
    ON casemesh.health_absence_records USING gin (to_tsvector('simple', neutral_label));

CREATE INDEX adjustment_requests_matter_fts_ix
    ON casemesh.adjustment_requests USING gin (to_tsvector('simple', neutral_label));

CREATE INDEX workplace_processes_matter_fts_ix
    ON casemesh.workplace_processes USING gin (to_tsvector('simple', stage_label));
