use SourceProducer::{make, consume};

fn main() {
    let value = make();
    consume(value);
}
