# QUIC TCP Reverse Tunnel

Creates TCP reverse tunnels over QUIC from a public server to a private client.

## Mecahnism

A private client requests a public server to listen on a certain TCP port, and open one QUIC bi-directional stream per TCP connection from the public.
Upon accepting a QUIC bi-directional stream, the client binds it to a local TCP connection.

## Example

### Basic Setup

- Public server listens on UDP port 7878.
- Private client forwards public TCP port 80 to private TCP port 5000.
- Private server listens on `http://127.0.0.1:5000`.
- FQDN `example.com` points to the public server.

### Basic Usage

A smart phone browser loads `http://example.com` over the public Internet and gets a response served by the private server.

### Advanced Setup

- (basic setup above)
- Public HTTPS reverse proxy (e.g. CloudFlare) forwards HTTPS traffic to HTTP.
- Private HTTP reverse proxy (e.g. NGINX) forwards to different ports by host name or path matching.
- Multiple instances of private server listens on different ports.

### Advanced Usage

A tester tests multiple web services and webhooks hosted on a development machine.

## See Also

Check out [awesome-tunneling](https://github.com/anderspitman/awesome-tunneling) for advanced alternatives.
