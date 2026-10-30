#!/usr/bin/env ruby
# Structural lint of .github/workflows/*.yml (ADR-0059). NOT actionlint: it does not type-check expressions or know every action input.
# It enforces what this repository promises about its pipeline, and fails on:
#   - YAML that does not parse; a missing name/on/permissions at the top; a job without runs-on (or uses) or without timeout-minutes
#   - `uses:` of a third-party action that is not pinned to a 40-character commit SHA (local ./ and reusable ./ workflows are exempt)
#   - `pull_request_target` or `workflow_run` triggers (they run with secrets on untrusted code), `permissions: write-all`
#   - `needs:` naming a job that does not exist, a reusable workflow that does not exist, a `with:` input the callee does not declare
#   - `${{ github.event.* }}`, `github.head_ref` or `inputs.*` interpolated inside a `run:` script (shell injection): pass through `env:` instead
#   - `run:` steps that call a repository script (scripts/...) which does not exist or is not executable
#   - a step that has neither `run` nor `uses`; duplicate step ids; `secrets.` echoed
# Ruby standard library only (YAML). Exit 1 on any finding.
require "yaml"
require "pathname"

root = Pathname.new(__dir__).join("..", "..").cleanpath
dir = ARGV[0] ? Pathname.new(ARGV[0]) : root.join(".github", "workflows")
errors = []
files = Dir[dir.join("*.yml").to_s].sort
errors << "no workflow files in #{dir}" if files.empty?

def trigger(doc)
  doc.key?("on") ? doc["on"] : doc[true] # YAML 1.1 reads the bare key `on` as true
end

workflows = {}
files.each do |f|
  begin
    workflows[File.basename(f)] = YAML.load(File.read(f))
  rescue StandardError => e
    errors << "#{File.basename(f)}: YAML does not parse: #{e.message}"
  end
end

workflows.each do |name, doc|
  err = ->(msg) { errors << "#{name}: #{msg}" }
  unless doc.is_a?(Hash)
    err.call("not a mapping")
    next
  end
  err.call("missing top-level name") unless doc["name"].is_a?(String)
  on = trigger(doc)
  err.call("missing trigger (on)") if on.nil?
  triggers = on.is_a?(Hash) ? on.keys : Array(on)
  %w[pull_request_target workflow_run].each { |t| err.call("trigger #{t} is not allowed (runs with secrets on untrusted code)") if triggers.include?(t) }
  perms = doc["permissions"]
  err.call("missing top-level permissions (least privilege: contents: read)") if perms.nil?
  err.call("permissions: write-all is not allowed") if perms == "write-all"
  jobs = doc["jobs"]
  unless jobs.is_a?(Hash) && !jobs.empty?
    err.call("no jobs")
    next
  end
  jobs.each do |jid, job|
    j = ->(msg) { errors << "#{name}: job #{jid}: #{msg}" }
    if job.key?("uses")
      callee = job["uses"]
      if callee.start_with?("./")
        path = root.join(callee.sub("./", ""))
        if !path.exist?
          j.call("reusable workflow #{callee} does not exist")
        else
          callee_doc = workflows[File.basename(callee)]
          declared = (callee_doc && trigger(callee_doc).is_a?(Hash) && trigger(callee_doc)["workflow_call"].is_a?(Hash)) ? (trigger(callee_doc)["workflow_call"]["inputs"] || {}).keys : nil
          j.call("#{callee} has no workflow_call trigger") if callee_doc && !(trigger(callee_doc).is_a?(Hash) && trigger(callee_doc).key?("workflow_call"))
          (job["with"] || {}).each_key { |k| j.call("passes input #{k}, which #{callee} does not declare") if declared && !declared.include?(k) }
        end
      elsif callee !~ /@[0-9a-f]{40}\b/
        j.call("reusable workflow #{callee} is not pinned to a commit SHA")
      end
    else
      j.call("missing runs-on") unless job["runs-on"]
      j.call("missing timeout-minutes") unless job["timeout-minutes"]
      ids = []
      (job["steps"] || []).each_with_index do |s, i|
        sn = "step #{i + 1}#{s["name"] ? " (#{s["name"]})" : ""}"
        s2 = ->(msg) { errors << "#{name}: job #{jid}: #{sn}: #{msg}" }
        s2.call("has neither run nor uses") unless s["run"] || s["uses"]
        if s["id"]
          s2.call("duplicate step id #{s["id"]}") if ids.include?(s["id"])
          ids << s["id"]
        end
        if (u = s["uses"]) && !u.start_with?("./") && u !~ /\A[\w.-]+\/[\w.\/-]+@[0-9a-f]{40}(\s|\z)/
          s2.call("action #{u} is not pinned to a 40-character commit SHA")
        end
        if (r = s["run"])
          r.scan(/\$\{\{\s*([^}]+?)\s*\}\}/).flatten.each do |expr|
            s2.call("expression ${{ #{expr} }} inside run: is a shell-injection risk; pass it through env:") if expr =~ /\A(github\.event\.|github\.head_ref|inputs\.|steps\..*\.outputs\.)/
            s2.call("secret interpolated into a script; pass it through env:") if expr.start_with?("secrets.")
          end
          r.scan(%r{(?:^|[\s(])(\.?/?scripts/[\w./-]+\.(?:sh|py|rb))}).flatten.each do |script|
            path = root.join(script.sub(%r{\A\./}, ""))
            if !path.exist? then s2.call("calls #{script}, which does not exist")
            elsif !path.executable? then s2.call("calls #{script}, which is not executable")
            end
          end
        end
      end
    end
    Array(job["needs"]).each { |n| j.call("needs unknown job #{n}") unless jobs.key?(n) }
  end
end

errors.each { |e| warn "FAIL #{e}" }
puts "workflow lint: #{files.size} workflow(s), #{errors.size} problem(s)"
exit(errors.empty? ? 0 : 1)
