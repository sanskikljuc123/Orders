import os
import uuid

import httpx
from fastapi import Depends, FastAPI, Header, HTTPException
from pydantic import BaseModel, Field

app = FastAPI()
PAYMENT_URL = os.environ.get("PAYMENT_URL", "https://payments.example.com")
TOKENS = {"token-ana": "ana", "token-bob": "bob"}  # stand-in for real auth
ORDERS: dict[str, dict] = {}


def current_user(authorization: str | None = Header(default=None)) -> str:
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(401, "Missing bearer token")
    user = TOKENS.get(authorization.removeprefix("Bearer "))
    if user is None:
        raise HTTPException(401, "Invalid token")
    return user


class PaymentClient:
    async def charge(self, amount: float) -> bool:
        async with httpx.AsyncClient(base_url=PAYMENT_URL) as c:
            r = await c.post("/charge", json={"amount": amount})
            r.raise_for_status()
            return r.json()["approved"]


def get_payment() -> PaymentClient:
    return PaymentClient()


class CreateOrder(BaseModel):
    product: str = Field(min_length=1, max_length=50)
    quantity: int = Field(ge=1, le=100)


@app.post("/orders", status_code=201)
async def create_order(body: CreateOrder, user: str = Depends(current_user),
                       payment: PaymentClient = Depends(get_payment)):
    approved = await payment.charge(body.quantity * 10)
    order = {"id": str(uuid.uuid4()), "product": body.product, "quantity": body.quantity,
             "status": "Paid" if approved else "PaymentFailed", "user": user}
    ORDERS[order["id"]] = order
    return order


@app.get("/orders/{order_id}")
async def get_order(order_id: str, user: str = Depends(current_user)):
    order = ORDERS.get(order_id)
    if order is None or order["user"] != user:
        raise HTTPException(404, "Order not found")
    return order
