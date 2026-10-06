use SourceProducer::{make, consume};

fn pass(value: SourceProducer::Pair) -> SourceProducer::Pair { value }

fn main() {
    let pair = pass(make());
    if pair.right { println!("{}", consume(pair)); }
}
