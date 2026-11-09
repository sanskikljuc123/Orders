# Python (pytest)

Look up versions with `pip index versions <pkg>` and put them where the repo pins dependencies
(`pyproject.toml`, `requirements-dev.txt`, Poetry/uv groups).

## Layout and config

```
tests/
  unit/  integration/  e2e/
  conftest.py          # shared fixtures
```

```toml
[tool.pytest.ini_options]
testpaths = ["tests"]
addopts = "-ra --strict-markers"                  # with pytest-randomly installed, order is shuffled (seed printed; replay with --randomly-seed=N)
markers = ["smoke", "critical", "integration", "e2e"]
timeout = 30                                     # pytest-timeout, config-level
```
Coverage gate: `pytest --cov=<pkg> --cov-branch --cov-fail-under=<current>` (pytest-cov).

## Unit

```python
@pytest.mark.parametrize("qty", [0, 101, -5])
def test_quantity_outside_1_to_100_is_rejected(qty):
    with pytest.raises(ValidationError, match="Quantity must be between 1 and 100"):
        CreateOrder(product="Book", quantity=qty)
```
- `pytest.raises(..., match=...)` — never `try: ... except: pass`.
- Time: `freezegun` / `time-machine`, or inject a clock. Randomness: seeded `random.Random(42)`.
- Mocks: `unittest.mock` / `pytest-mock` at the boundary (`mocker.patch("app.payments.client.charge")`),
  patch where the name is *looked up*.

## Fixtures with teardown

```python
@pytest.fixture(scope="session")
def postgres():
    with PostgresContainer("postgres:17") as pg:     # testcontainers-python
        yield pg.get_connection_url()

@pytest.fixture
def order(api_client):
    o = api_client.post("/orders", json={"product": f"Book-{uuid4().hex}", "quantity": 1}).json()
    yield o
    api_client.delete(f"/orders/{o['id']}")            # runs even if the test fails
```

## HTTP

- FastAPI: `TestClient(app)` / `httpx.AsyncClient(transport=ASGITransport(app))`; override deps
  with `app.dependency_overrides[get_payment] = lambda: FakePayment()` and clear in teardown.
- Django: `pytest-django`, `client` fixture, `@pytest.mark.django_db`.
- Outbound HTTP: `respx` (httpx) or `responses` (requests); fail on unmatched requests.

## E2E

`pytest-playwright`: `page` fixture, `base_url` in config (`--base-url`), `expect(page.get_by_role(...)).to_have_text(...)`,
`--tracing retain-on-failure --video retain-on-failure --screenshot only-on-failure`. No `time.sleep`
or `page.wait_for_timeout`.
