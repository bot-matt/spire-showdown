set shell := ["bash", "-cu"]

test:
    cd bridge && cargo test

doctor:
    cd bridge && cargo run -- doctor

format-check:
    cd bridge && cargo fmt --check

lint:
    cd bridge && cargo clippy --all-targets -- -D warnings

