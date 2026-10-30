{{- define "keel.name" -}}keel{{- end -}}

{{- define "keel.fullname" -}}
{{- if contains "keel" .Release.Name -}}{{ .Release.Name | trunc 50 | trimSuffix "-" }}{{- else -}}{{ printf "%s-keel" .Release.Name | trunc 50 | trimSuffix "-" }}{{- end -}}
{{- end -}}

{{- define "keel.labels" -}}
app.kubernetes.io/name: {{ include "keel.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | quote }}
{{- end -}}

{{- define "keel.selectorLabels" -}}
app.kubernetes.io/name: {{ include "keel.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: web
{{- end -}}

{{- define "keel.migrateLabels" -}}
app.kubernetes.io/name: {{ include "keel.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: migrate
{{- end -}}

{{- define "keel.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}{{ default (include "keel.fullname" .) .Values.serviceAccount.name }}{{- else -}}{{ default "default" .Values.serviceAccount.name }}{{- end -}}
{{- end -}}

{{/* Image reference: digest wins over tag. */}}
{{- define "keel.image" -}}
{{- if .Values.image.digest -}}{{ .Values.image.repository }}@{{ .Values.image.digest }}{{- else -}}{{ .Values.image.repository }}:{{ default .Chart.AppVersion .Values.image.tag }}{{- end -}}
{{- end -}}

{{- define "keel.appSecret" -}}
{{- if .Values.dev.enabled -}}{{ include "keel.fullname" . }}-dev{{- else -}}{{ required "secrets.app.existingSecret is required (see docs/ops/kubernetes.md)" .Values.secrets.app.existingSecret }}{{- end -}}
{{- end -}}

{{- define "keel.migratorSecret" -}}
{{- if .Values.dev.enabled -}}{{ include "keel.fullname" . }}-dev-migrator{{- else -}}{{ required "secrets.migrator.existingSecret is required (see docs/ops/kubernetes.md)" .Values.secrets.migrator.existingSecret }}{{- end -}}
{{- end -}}

{{- define "keel.dpClaim" -}}
{{- default (printf "%s-dataprotection" (include "keel.fullname" .)) .Values.dataProtection.persistence.existingClaim -}}
{{- end -}}

{{/* Non-secret environment shared by the web pods. Secrets come from files in KEEL_SECRETS_DIR. */}}
{{- define "keel.env" -}}
- { name: ASPNETCORE_ENVIRONMENT, value: Production }
- { name: ASPNETCORE_URLS, value: "http://+:8080" }
- { name: KEEL_SECRETS_DIR, value: /run/secrets/keel }
- { name: Keel__DataProtection__KeyDirectory, value: /var/lib/keel/dataprotection }
- { name: Keel__Auth__Authority, value: {{ required "config.auth.authority is required" .Values.config.auth.authority | quote }} }
- { name: Keel__Auth__Audience, value: {{ .Values.config.auth.audience | quote }} }
- { name: Keel__Auth__WebClientId, value: {{ .Values.config.auth.webClientId | quote }} }
- { name: Keel__Auth__RequireHttpsMetadata, value: {{ .Values.config.auth.requireHttpsMetadata | quote }} }
{{- with .Values.config.auth.signInMethod }}
- { name: Keel__Auth__SignIn__Method, value: {{ . | quote }} }
{{- end }}
{{- if .Values.config.auth.passwordFallback }}
- { name: Keel__Auth__SignIn__PasswordFallback, value: "true" }
{{- end }}
{{- with .Values.config.auth.stepUpAcr }}
- { name: Keel__Auth__StepUpAcr, value: {{ . | quote }} }
{{- end }}
- { name: Keel__Security__RequireFips, value: {{ .Values.config.security.requireFips | quote }} }
- { name: Keel__Vault__ServiceUrl, value: {{ required "config.vault.serviceUrl is required" .Values.config.vault.serviceUrl | quote }} }
- { name: Keel__Vault__Bucket, value: {{ .Values.config.vault.bucket | quote }} }
- { name: Keel__Vault__Region, value: {{ .Values.config.vault.region | quote }} }
- { name: Keel__Vault__ServerSideEncryption, value: {{ .Values.config.vault.serverSideEncryption | quote }} }
- { name: Keel__Vault__MaxUploadBytes, value: {{ .Values.config.vault.maxUploadBytes | int64 | quote }} }
- { name: Keel__Vault__PartSizeBytes, value: {{ .Values.config.vault.partSizeBytes | int64 | quote }} }
- { name: Keel__Vault__MaxPdfStampBytes, value: {{ .Values.config.vault.maxPdfStampBytes | int64 | quote }} }
- { name: Keel__Vault__RequestTimeoutSeconds, value: {{ .Values.config.vault.requestTimeoutSeconds | int64 | quote }} }
- { name: Keel__Audit__CheckpointInterval, value: {{ .Values.config.audit.checkpointInterval | quote }} }
- { name: Keel__Audit__CheckpointPrivateKeyPath, value: /run/secrets/keel/audit_checkpoint_key.pem }
{{- if not .Values.dev.enabled }}
- { name: Keel__Audit__CheckpointPublicKeyPaths__0, value: /run/secrets/keel/audit_checkpoint_key.pub.pem }
{{- end }}
- { name: Keel__Workflow__StepUpMaxAgeSeconds, value: {{ .Values.config.workflow.stepUpMaxAgeSeconds | int64 | quote }} }
- { name: Keel__Workflow__DirectoryRefreshSeconds, value: {{ .Values.config.workflow.directoryRefreshSeconds | int64 | quote }} }
- { name: Keel__Proxy__Enabled, value: "true" }
{{- range $i, $n := .Values.config.proxy.knownNetworks }}
- { name: {{ printf "Keel__Proxy__KnownNetworks__%d" $i }}, value: {{ $n | quote }} }
{{- end }}
{{- if .Values.tls.caBundle.existingConfigMap }}
- { name: SSL_CERT_FILE, value: /etc/keel/ca/{{ .Values.tls.caBundle.key }} }
{{- end }}
{{- range $k, $v := .Values.config.extraEnv }}
- { name: {{ $k | quote }}, value: {{ $v | quote }} }
{{- end }}
{{- end -}}

{{- define "keel.caMount" -}}
{{- if .Values.tls.caBundle.existingConfigMap }}
- { name: ca, mountPath: /etc/keel/ca, readOnly: true }
{{- end }}
{{- end -}}

{{- define "keel.caVolume" -}}
{{- if .Values.tls.caBundle.existingConfigMap }}
- name: ca
  configMap: { name: {{ .Values.tls.caBundle.existingConfigMap }}, defaultMode: 0444 }
{{- end }}
{{- end -}}
