from __future__ import annotations

import unittest

from amlyrics.contracts import IpcMessage, MessageType
from amlyrics.ipc import LocalIpcClient, LocalIpcServer


class IpcTransportTests(unittest.TestCase):
    def test_local_request_response(self) -> None:
        def handler(message: IpcMessage) -> IpcMessage:
            return IpcMessage(MessageType.DIAGNOSTIC, {"echo": message.payload})

        server = LocalIpcServer(handler)
        address = server.start()
        try:
            response = LocalIpcClient(address).request(IpcMessage(MessageType.SHOW_OVERLAY, {"enabled": True}))
            self.assertIsNotNone(response)
            self.assertEqual(response.payload["echo"]["enabled"], True)
        finally:
            server.stop()


if __name__ == "__main__":
    unittest.main()
