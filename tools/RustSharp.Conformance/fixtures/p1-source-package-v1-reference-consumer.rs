use SourceProducer::{identity, add};

fn main() {
    let mut value = 41;
    add(&mut value);
    let view = identity(&value);
    println!("{}", *view);
}
