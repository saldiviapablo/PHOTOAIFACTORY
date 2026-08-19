from typing import Any
from pydantic import BaseModel, Field

class AiRequest(BaseModel):
    api_version: str = "v1"
    request_id: str
    job_id: str
    operation: str
    input_paths: list[str] = Field(default_factory=list)
    config: dict[str, Any] = Field(default_factory=dict)

class AiError(BaseModel):
    code: str
    category: str
    retryable: bool
    component: str = "python-ai-worker"
    message: str
    details: dict[str, Any] | None = None

class AiResponse(BaseModel):
    api_version: str = "v1"
    request_id: str
    success: bool
    result: dict[str, Any] | None = None
    error: AiError | None = None
    timings: dict[str, float] = Field(default_factory=dict)
