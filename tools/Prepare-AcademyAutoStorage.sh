#!/usr/bin/env bash
# Only replace never-bound claims from the failed standard-driver bootstrap.
set -euo pipefail
claims=(gearflow-billing-keys data-billing-mongo-0)
statefulsets=(billing-mongo)
migrate=false
for claim in "${claims[@]}"; do
  data=$(kubectl get pvc "$claim" -n gearflow --ignore-not-found -o json)
  if [ -z "$data" ]; then continue; fi
  class=$(jq -r '.spec.storageClassName // ""' <<< "$data")
  if [ "$class" = gearflow-gp3-auto ]; then continue; fi
  if [ "$class" != gearflow-gp3 ] || \
     ! jq -e '.status.phase == "Pending" and ((.spec.volumeName // "") == "")' <<< "$data" >/dev/null; then
    echo "Refusing to replace PVC $claim: it may contain data or use another storage class. Migrate it explicitly."
    exit 1
  fi
  migrate=true
done
if [ "$migrate" != true ]; then exit 0; fi
wait_for_pods() {
  local selector=$1
  if [ -n "$(kubectl get pods -l "$selector" -n gearflow -o name)" ]; then
    kubectl wait --for=delete pod -l "$selector" -n gearflow --timeout=180s
  fi
}
# Claims and StatefulSet templates are immutable. Stop consumers before replacing
# only the empty pending claims; do not touch secrets or bound data volumes.
if [ -n "$(kubectl get deployment gearflow-billing -n gearflow --ignore-not-found -o name)" ]; then
  kubectl scale deployment gearflow-billing -n gearflow --replicas=0
  wait_for_pods app=gearflow-billing
fi
kubectl delete statefulset "${statefulsets[@]}" -n gearflow --ignore-not-found --wait=true
wait_for_pods app=billing-mongo
for claim in "${claims[@]}"; do
  data=$(kubectl get pvc "$claim" -n gearflow --ignore-not-found -o json)
  if [ -z "$data" ]; then continue; fi
  if jq -e '.spec.storageClassName == "gearflow-gp3"' <<< "$data" >/dev/null; then
    # Recheck immediately before deleting; never remove a bound claim.
    jq -e '.status.phase == "Pending" and ((.spec.volumeName // "") == "")' <<< "$data" >/dev/null
    kubectl delete pvc "$claim" -n gearflow --wait=true --timeout=180s
  fi
done
