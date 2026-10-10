"""Create short-lived private mTLS credentials for isolated local benchmarks."""
from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
import ipaddress
from pathlib import Path

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    target = args.output_dir.expanduser().resolve()
    target.mkdir(parents=True, exist_ok=True, mode=0o700)
    if any(target.iterdir()):
        raise SystemExit("Choose an empty private directory; existing credentials are never overwritten.")
    now = datetime.now(timezone.utc)

    def key():
        return rsa.generate_private_key(public_exponent=65537, key_size=2048)

    def certificate(subject, public_key, issuer, issuer_key, ca=False, server=False, client=False):
        builder = (x509.CertificateBuilder().subject_name(subject).issuer_name(issuer)
                   .public_key(public_key).serial_number(x509.random_serial_number())
                   .not_valid_before(now - timedelta(minutes=5)).not_valid_after(now + timedelta(days=2))
                   .add_extension(x509.BasicConstraints(ca=ca, path_length=0 if ca else None), critical=True))
        if server:
            name = subject.get_attributes_for_oid(NameOID.COMMON_NAME)[0].value
            builder = builder.add_extension(x509.SubjectAlternativeName([
                x509.DNSName(name), x509.DNSName("localhost"), x509.IPAddress(ipaddress.ip_address("127.0.0.1"))]), critical=False)
        if server or client:
            builder = builder.add_extension(x509.ExtendedKeyUsage([
                ExtendedKeyUsageOID.SERVER_AUTH if server else ExtendedKeyUsageOID.CLIENT_AUTH]), critical=False)
        return builder.sign(issuer_key, hashes.SHA256())

    def name(common_name):
        return x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, common_name)])

    def save(path, value):
        with path.open("xb") as stream:
            stream.write(value)
        path.chmod(0o600)

    for service in ("table", "latex"):
        directory = target / f"{service}-communication"
        directory.mkdir(mode=0o700)
        for role, ca_file, leaf_file, key_file in (
            ("server", "worker-server-ca.pem", "worker-server.pem", "worker-server-key.pem"),
            ("client", "api-client-ca.pem", "client.pem", "client-key.pem"),
        ):
            ca_key = key()
            ca_name = name(f"Local benchmark {service} {role} CA")
            ca_cert = certificate(ca_name, ca_key.public_key(), ca_name, ca_key, ca=True)
            leaf_key = key()
            leaf_name = name(f"snowshot-{service}-rec" if role == "server" else "local-benchmark-client")
            leaf_cert = certificate(leaf_name, leaf_key.public_key(), ca_name, ca_key,
                                    server=role == "server", client=role == "client")
            save(directory / ca_file, ca_cert.public_bytes(serialization.Encoding.PEM))
            save(directory / leaf_file, leaf_cert.public_bytes(serialization.Encoding.PEM))
            save(directory / key_file, leaf_key.private_bytes(serialization.Encoding.PEM,
                 serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    print(f"Created local benchmark mTLS credentials in {target}")


if __name__ == "__main__":
    main()
