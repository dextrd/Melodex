FROM python:3.13-slim

WORKDIR /plugin
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt \
    && mkdir -p /staging && chmod 1777 /staging
COPY app app

ENV STAGING_ROOT=/staging PARALLEL_FETCHES=4
VOLUME /staging
EXPOSE 8080
USER 1000:1000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080"]
