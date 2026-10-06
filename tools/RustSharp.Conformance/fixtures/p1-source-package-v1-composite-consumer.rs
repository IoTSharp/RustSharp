use SourceProducer::pair;
fn main() {
    let owner = (40, 2);
    let view = pair(&owner);
    println!("{}", *view.0 + *view.1);
}
