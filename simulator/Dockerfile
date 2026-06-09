FROM python:3.12-slim AS runtime
WORKDIR /app

RUN pip install --no-cache-dir requests

COPY simulator/zigbee_simulator.py .

RUN groupadd -r appgroup && useradd -r -g appgroup appuser
USER appuser

ENTRYPOINT ["python", "zigbee_simulator.py"]
