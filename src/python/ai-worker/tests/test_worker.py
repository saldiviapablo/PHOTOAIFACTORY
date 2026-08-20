import os
os.environ["PAF_AI_TOKEN"]="test-token"

from fastapi.testclient import TestClient
import numpy as np
import cv2
from pathlib import Path
import tempfile
from photo_ai_worker.main import app

client=TestClient(app)
HEAD={"Authorization":"Bearer test-token"}

def make_image():
    fd, name = tempfile.mkstemp(suffix=".jpg")
    os.close(fd)
    p=Path(name)
    img=np.zeros((100,100,3),dtype=np.uint8)
    cv2.rectangle(img,(20,20),(80,80),(220,220,220),-1)
    cv2.imwrite(str(p),img)
    return p

def test_auth_required():
    assert client.get("/v1/health").status_code == 401

def test_health():
    r=client.get("/v1/health",headers=HEAD)
    assert r.status_code==200
    assert r.json()["status"]=="HEALTHY"

def test_analyze():
    p=make_image()
    try:
        r=client.post("/v1/analyze",headers=HEAD,json={"api_version":"v1","request_id":"r1","job_id":"j1","operation":"analyze","input_paths":[str(p)],"config":{}})
        data=r.json(); assert data["success"] is True
        assert data["result"]["technical"]["width"]==100
    finally: p.unlink(missing_ok=True)

def test_phase4_recipe_contract_is_conservative():
    r=client.post(
        "/v1/recipe/pre-ai",
        headers=HEAD,
        json={
            "api_version":"v1",
            "request_id":"r2",
            "job_id":"j1",
            "operation":"recipe.pre-ai",
            "input_paths":[],
            "config":{
                "schema_version":1,
                "analysis":{"schema_version":1,"technical":{}},
            },
        },
    )
    data=r.json()
    assert data["success"] is True
    assert data["request_id"]=="r2"
    assert data["result"]["recipe_version"]=="phase4-pre-ai-v1"
    assert data["result"]["operations"]==[]
