from fastapi import Header, HTTPException
from .settings import settings

def require_token(authorization: str | None = Header(default=None)) -> None:
    if not settings.token:
        raise HTTPException(status_code=503, detail="PAF_AI_TOKEN is not configured")
    expected = f"Bearer {settings.token}"
    if authorization != expected:
        raise HTTPException(status_code=401, detail="Invalid session token")
