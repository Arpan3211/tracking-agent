from fastapi import APIRouter

from app.api.v1 import admin, alerts, auth, devices, ingest, reports, websocket

api_router = APIRouter()
api_router.include_router(ingest.router, tags=["ingestion"])
api_router.include_router(auth.router)
api_router.include_router(devices.router)
api_router.include_router(alerts.router)
api_router.include_router(reports.router)
api_router.include_router(admin.router)
api_router.include_router(websocket.router, tags=["websocket"])
