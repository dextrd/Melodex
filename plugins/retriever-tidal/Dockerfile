FROM python:3.13-slim

RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /plugin
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt \
    && mkdir -p /staging /data && chmod 1777 /staging /data
COPY app app

ENV STAGING_ROOT=/staging DATA_ROOT=/data PARALLEL_FETCHES=2
VOLUME /staging /data
EXPOSE 8080
USER 1000:1000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080"]
