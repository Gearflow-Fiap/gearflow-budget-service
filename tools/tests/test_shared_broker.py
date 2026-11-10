import base64
import importlib.util
import io
from contextlib import redirect_stdout
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("sync", Path(__file__).parents[1] / "Sync-SharedBrokerSecret.py")
sync = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sync)


def encoded(value):
    return base64.b64encode(value.encode()).decode()


class SharedBrokerTests(unittest.TestCase):
    def test_copies_only_service_credentials_with_concurrency_check(self):
        source = {"data": {"username": encoded("gearflow_budget"), "password": encoded("private-password")}}
        target = {"metadata": {"resourceVersion": "42"}, "data": {"Jwt__Secret": encoded("unchanged")}}
        with patch.object(sync, "kubectl", side_effect=[source, target, {}]) as kube:
            output = io.StringIO()
            with redirect_stdout(output):
                sync.main()
        import json
        change = json.loads(kube.call_args.kwargs["payload"])
        self.assertEqual(change["metadata"]["resourceVersion"], "42")
        self.assertEqual(set(change["data"]), {"RabbitMq__Username", "RabbitMq__Password"})
        self.assertNotIn("private-password", output.getvalue())
        self.assertNotIn(encoded("private-password"), output.getvalue())

    def test_refuses_admin_credentials_without_touching_target(self):
        source = {"data": {"username": encoded("gearflow_admin"), "password": encoded("private-password")}}
        with patch.object(sync, "kubectl", return_value=source) as kube:
            with self.assertRaises(RuntimeError):
                sync.main()
        self.assertEqual(kube.call_count, 1)

    def test_missing_source_does_not_write_target(self):
        with patch.object(sync, "kubectl", side_effect=RuntimeError("unavailable")) as kube:
            with self.assertRaises(RuntimeError):
                sync.main()
        self.assertEqual(kube.call_count, 1)


if __name__ == "__main__":
    unittest.main()
