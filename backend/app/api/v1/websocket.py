import logging

from fastapi import APIRouter, WebSocket, WebSocketDisconnect
from jwt import PyJWTError

from app.api.v1.auth import ACCESS_COOKIE
from app.core.security import TokenType, decode_token

logger = logging.getLogger(__name__)
router = APIRouter()


class AlertConnectionManager:
    def __init__(self) -> None:
        self._connections: set[WebSocket] = set()

    async def connect(self, websocket: WebSocket) -> None:
        await websocket.accept()
        self._connections.add(websocket)

    def disconnect(self, websocket: WebSocket) -> None:
        self._connections.discard(websocket)

    async def broadcast(self, payload: dict) -> None:
        dead = set()
        for connection in self._connections:
            try:
                await connection.send_json(payload)
            except Exception:
                dead.add(connection)
        self._connections -= dead


# Process-local singleton - fine for a single API instance (pilot scale). If
# this ever runs behind multiple API replicas, broadcasting needs to move to
# a shared pub/sub (e.g. Postgres LISTEN/NOTIFY or Redis) instead, since each
# replica would otherwise only see the alerts it personally created.
alert_manager = AlertConnectionManager()


@router.websocket("/ws/alerts")
async def alerts_websocket(websocket: WebSocket) -> None:
    # Same cookie-based auth as the REST endpoints (see app/api/deps.py's
    # get_current_user) - WebSocket handshakes carry cookies too, so this
    # mirrors that check rather than accepting unauthenticated connections.
    access_token = websocket.cookies.get(ACCESS_COOKIE)
    if access_token is None:
        await websocket.close(code=1008)
        return

    try:
        payload = decode_token(access_token)
    except PyJWTError:
        await websocket.close(code=1008)
        return

    if payload.get("type") != TokenType.access:
        await websocket.close(code=1008)
        return

    await alert_manager.connect(websocket)
    try:
        while True:
            # Clients don't need to send anything meaningful - this just
            # keeps the connection open and detects disconnects.
            await websocket.receive_text()
    except WebSocketDisconnect:
        alert_manager.disconnect(websocket)
