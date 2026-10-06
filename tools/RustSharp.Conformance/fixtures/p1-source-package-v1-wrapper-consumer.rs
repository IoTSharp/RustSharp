use SourceWrapper::{make_pair, make_array, make_unit, read_unit};
fn forward(value: SourceProducer::Unit) -> SourceProducer::Unit { value }
fn main() {
    let pair = make_pair();
    println!("{}", pair.1);
    println!("{}", pair.0);
    let values = make_array();
    println!("{}", values[0] + values[1]);
    let value = forward(make_unit());
    println!("{}", read_unit(value));
}
