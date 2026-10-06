use SourceProducer::{Pair, Tuple, Unit, read, tuple, unit};
fn main() {
    let value: Pair = Pair { left: 42, right: true };
    println!("{}", read(value));
    println!("{}", tuple(Tuple(42)));
    println!("{}", unit(Unit));
}
