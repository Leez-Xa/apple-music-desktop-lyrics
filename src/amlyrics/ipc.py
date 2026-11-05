from __future__ import annotations

import json
import socket
import threading
from collections.abc import Callable

from .contracts import IpcMessage


class JsonLineCodec:
    @staticmethod
    def encode(message: IpcMessage) -> bytes:
        return (json.dumps(message.to_dict(), ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")

    @staticmethod
    def decode(line: bytes | str) -> IpcMessage:
        if isinstance(line, bytes):
            line = line.decode("utf-8")
        return IpcMessage.from_dict(json.loads(line))


class LocalIpcServer:
    """Local JSON-lines transport; a named pipe can replace it later."""

    def __init__(self, handler: Callable[[IpcMessage], IpcMessage | None], host: str = "127.0.0.1", port: int = 0) -> None:
        self.handler = handler
        self.host = host
        self.port = port
        self._server: socket.socket | None = None
        self._thread: threading.Thread | None = None
        self._stop = threading.Event()

    @property
    def address(self) -> tuple[str, int] | None:
        return self._server.getsockname() if self._server is not None else None

    def start(self) -> tuple[str, int]:
        if self._server is not None:
            raise RuntimeError("IPC server is already running")
        self._server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._server.bind((self.host, self.port))
        self._server.listen()
        self._server.settimeout(0.25)
        self._stop.clear()
        self._thread = threading.Thread(target=self._serve, name="amlyrics-ipc", daemon=True)
        self._thread.start()
        return self._server.getsockname()

    def stop(self) -> None:
        self._stop.set()
        if self._server is not None:
            self._server.close()
        if self._thread is not None:
            self._thread.join(timeout=1.0)
        self._server = None
        self._thread = None

    def _serve(self) -> None:
        assert self._server is not None
        while not self._stop.is_set():
            try:
                client, _ = self._server.accept()
            except (socket.timeout, OSError):
                continue
            with client:
                client_file = client.makefile("rwb")
                for line in client_file:
                    if not line.strip():
                        continue
                    response = self.handler(JsonLineCodec.decode(line))
                    if response is not None:
                        client_file.write(JsonLineCodec.encode(response))
                        client_file.flush()


class LocalIpcClient:
    def __init__(self, address: tuple[str, int]) -> None:
        self.address = address

    def request(self, message: IpcMessage, *, timeout: float = 1.0) -> IpcMessage | None:
        with socket.create_connection(self.address, timeout=timeout) as client:
            client_file = client.makefile("rwb")
            client_file.write(JsonLineCodec.encode(message))
            client_file.flush()
            response = client_file.readline()
            return JsonLineCodec.decode(response) if response else None
