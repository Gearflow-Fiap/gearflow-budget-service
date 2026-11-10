"""Copy only the Billing service credentials from the broker, without logging secrets."""
import base64
import json
import subprocess


def kubectl(*args, payload=None):
    result = subprocess.run(["kubectl", *args], input=payload, text=True,
                            capture_output=True, check=False)
    if result.returncode:
        raise RuntimeError("Cannot read or update shared broker credentials; check cluster, namespace and access.")
    return json.loads(result.stdout) if result.stdout.strip() else None


def main():
    source = kubectl("get", "secret", "rabbitmq-budget-credentials", "-n", "messaging", "-o", "json")["data"]
    username = base64.b64decode(source["username"], validate=True).decode()
    if username != "gearflow_budget" or not base64.b64decode(source["password"], validate=True):
        raise RuntimeError("Shared broker Billing credentials are incomplete or have an unexpected username.")
    target = kubectl("get", "secret", "gearflow-billing-secrets", "-n", "gearflow", "-o", "json")
    # Use resourceVersion for optimistic concurrency; never copy admin/other service credentials.
    patch = {"metadata": {"resourceVersion": target["metadata"]["resourceVersion"]}, "data": {
        "RabbitMq__Username": source["username"], "RabbitMq__Password": source["password"]}}
    kubectl("patch", "secret", "gearflow-billing-secrets", "-n", "gearflow",
            "--type=merge", "--patch-file=/dev/stdin", "-o", "json", payload=json.dumps(patch))
    print("Billing service credentials synchronized from messaging; secret values were not logged.")


if __name__ == "__main__":
    main()
