{{/*
Expand the name of the chart.
*/}}
{{- define "jellyfin-ha.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
We truncate at 63 chars because some Kubernetes name fields are limited to this (by the DNS naming spec).
If release name contains chart name it will be used as a full name.
*/}}
{{- define "jellyfin-ha.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Create chart label.
*/}}
{{- define "jellyfin-ha.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels.
*/}}
{{- define "jellyfin-ha.labels" -}}
helm.sh/chart: {{ include "jellyfin-ha.chart" . }}
{{ include "jellyfin-ha.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels.
*/}}
{{- define "jellyfin-ha.selectorLabels" -}}
app.kubernetes.io/name: {{ include "jellyfin-ha.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: server
{{- end }}

{{/*
Service account name.
*/}}
{{- define "jellyfin-ha.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "jellyfin-ha.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
Fully qualified name of the in-cluster Redis service.
*/}}
{{- define "jellyfin-ha.redis.fullname" -}}
{{- printf "%s-redis" (include "jellyfin-ha.fullname" .) }}
{{- end }}

{{/*
Fully qualified name of the in-cluster PostgreSQL service.
*/}}
{{- define "jellyfin-ha.postgres.fullname" -}}
{{- printf "%s-postgres" (include "jellyfin-ha.fullname" .) }}
{{- end }}

{{/*
Compute the Redis connection string.
Priority:
  1. existingSecret (mounted as env var in the statefulset template)
  2. explicit ha.transcodeStore.redisConnectionString value
  3. auto-compose from the in-cluster Redis service name when redis.enabled=true
Returns empty string if none of the above apply (= single-instance / NullStore mode).
This helper returns the literal string only for cases 2 and 3; case 1 is handled
directly in the container env block via secretKeyRef.
*/}}
{{- define "jellyfin-ha.redisConnectionString" -}}
{{- if .Values.ha.transcodeStore.redisConnectionString }}
{{- .Values.ha.transcodeStore.redisConnectionString }}
{{- else if .Values.redis.enabled }}
{{- printf "%s:6379,abortConnect=false" (include "jellyfin-ha.redis.fullname" .) }}
{{- end }}
{{- end }}

{{/*
Return true if HA mode is active and Redis should be wired up.
*/}}
{{- define "jellyfin-ha.haEnabled" -}}
{{- if and .Values.ha.enabled (or .Values.redis.enabled .Values.ha.transcodeStore.redisConnectionString .Values.ha.transcodeStore.existingSecret) }}
{{- "true" }}
{{- end }}
{{- end }}

{{/*
Config PVC claim name — either the existing claim or the chart-managed one.
*/}}
{{- define "jellyfin-ha.configPvcName" -}}
{{- if .Values.persistence.config.existingClaim }}
{{- .Values.persistence.config.existingClaim }}
{{- else }}
{{- printf "%s-config" (include "jellyfin-ha.fullname" .) }}
{{- end }}
{{- end }}

{{/*
Transcode PVC claim name — either the existing claim or the chart-managed one.
*/}}
{{- define "jellyfin-ha.transcodePvcName" -}}
{{- if .Values.persistence.transcode.existingClaim }}
{{- .Values.persistence.transcode.existingClaim }}
{{- else }}
{{- printf "%s-transcode" (include "jellyfin-ha.fullname" .) }}
{{- end }}
{{- end }}

{{/*
Media PVC claim name — either the existing claim or the chart-managed NFS PVC.
*/}}
{{- define "jellyfin-ha.mediaPvcName" -}}
{{- if .Values.persistence.media.existingClaim }}
{{- .Values.persistence.media.existingClaim }}
{{- else if .Values.persistence.media.nfs.enabled }}
{{- printf "%s-media" (include "jellyfin-ha.fullname" .) }}
{{- end }}
{{- end }}
